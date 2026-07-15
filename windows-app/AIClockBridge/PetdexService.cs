using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AIClockBridge;

// Pet animation source: petdex.dev's public manifest (3300+ open-source
// "Codex pets"). Each pet is one 1536x1872 WebP spritesheet laid out on a
// fixed 8x9 grid of 192x208 frames; each row is one named animation (the
// row/frame-count table below mirrors petdex's own pet-states definition).
// We crop a row, composite the frames onto black at the device slot size,
// encode a looping GIF, and POST it to the clock, which re-decodes it
// on-device into its own format. ImageSharp does the WebP decode + GIF
// encode (System.Drawing can do neither).

record PetdexPet(string Slug, string DisplayName, string Kind, string SpritesheetUrl);

record PetdexAnimState(string Id, string Label, int Row, int Frames, int DurationMs);

static class PetdexService
{
    public const string ManifestUrl = "https://assets.petdex.dev/manifests/petdex-v1.json";

    public const int FrameW = 192, FrameH = 208;
    /// Device firmware caps custom animations at 8 frames (MAX_CUSTOM_FRAMES).
    public const int MaxFrames = 8;

    public static readonly PetdexAnimState[] States =
    {
        new("idle", "待机 Idle", 0, 6, 1100),
        new("running-right", "右跑 Run Right", 1, 8, 1060),
        new("running-left", "左跑 Run Left", 2, 8, 1060),
        new("waving", "挥手 Waving", 3, 4, 700),
        new("jumping", "跳跃 Jumping", 4, 5, 840),
        new("failed", "失败 Failed", 5, 8, 1220),
        new("waiting", "等待 Waiting", 6, 6, 1010),
        new("running", "原地跑 Running", 7, 6, 820),
        new("review", "思考 Review", 8, 6, 1030),
    };

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    static readonly HttpClient DirectHttp = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(90),
    };
    static readonly string ManifestCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIClockBridge", "petdex-v1.json");
    static List<PetdexPet> _cachedPets;

    public static async Task<List<PetdexPet>> LoadManifest()
    {
        if (_cachedPets != null) return _cachedPets;
        byte[] data;
        try
        {
            data = await DownloadBytes(ManifestUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestCachePath)!);
            await File.WriteAllBytesAsync(ManifestCachePath, data);
        }
        catch (Exception e)
        {
            try { data = await File.ReadAllBytesAsync(ManifestCachePath); }
            catch { throw new DeviceException($"petdex manifest 下载失败：{e.Message}"); }
        }
        try
        {
            using var doc = JsonDocument.Parse(data);
            var pets = new List<PetdexPet>();
            foreach (var item in doc.RootElement.GetProperty("pets").EnumerateArray())
            {
                if (!item.TryGetProperty("slug", out var slug)
                    || !item.TryGetProperty("spritesheetUrl", out var sheet)) continue;
                var slugStr = slug.GetString();
                pets.Add(new PetdexPet(
                    slugStr,
                    item.TryGetProperty("displayName", out var dn) ? dn.GetString() : slugStr,
                    item.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                    sheet.GetString()));
            }
            _cachedPets = pets;
            return pets;
        }
        catch (Exception)
        {
            throw new DeviceException("petdex manifest 解析失败");
        }
    }

    public static async Task<Image<Rgba32>> DownloadSpritesheet(PetdexPet pet)
    {
        byte[] data;
        try
        {
            data = await DownloadBytes(pet.SpritesheetUrl);
        }
        catch (Exception e)
        {
            throw new DeviceException($"spritesheet 下载失败：{e.Message}");
        }
        try
        {
            return SixLabors.ImageSharp.Image.Load<Rgba32>(data);
        }
        catch (Exception)
        {
            throw new DeviceException("spritesheet 解码失败（WebP）");
        }
    }

    static async Task<byte[]> DownloadBytes(string url)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var pending = new List<Task<byte[]>>
        {
            Http.GetByteArrayAsync(url, cts.Token),
            DirectHttp.GetByteArrayAsync(url, cts.Token),
        };
        Exception lastError = null;
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            try
            {
                var result = await completed;
                cts.Cancel();
                return result;
            }
            catch (Exception e) { lastError = e; }
        }
        throw lastError ?? new HttpRequestException("下载失败");
    }

    /// Crops `state`'s row out of the sheet and encodes a looping GIF at
    /// targetW x targetH: frames aspect-fit, composited onto black (matches
    /// the clock's black background and avoids GIF transparency compositing
    /// surprises in the on-device decoder).
    public static byte[] BuildGif(Image<Rgba32> sheet, PetdexAnimState state,
                                  int targetW, int targetH)
    {
        var frameCount = Math.Min(state.Frames, MaxFrames);
        if (frameCount <= 0) return null;
        // GIF delays are centiseconds; floor at 5cs like the Mac app's 0.05s
        var delayCs = Math.Max(5, state.DurationMs / state.Frames / 10);

        // aspect-fit rect inside the target slot
        var scale = Math.Min((double)targetW / FrameW, (double)targetH / FrameH);
        var drawW = Math.Max(1, (int)Math.Round(FrameW * scale));
        var drawH = Math.Max(1, (int)Math.Round(FrameH * scale));
        var drawX = (targetW - drawW) / 2;
        var drawY = (targetH - drawH) / 2;

        try
        {
            using var gif = new Image<Rgba32>(targetW, targetH);
            gif.Metadata.GetGifMetadata().RepeatCount = 0; // loop forever

            for (int i = 0; i < frameCount; i++)
            {
                var crop = new SixLabors.ImageSharp.Rectangle(
                    i * FrameW, state.Row * FrameH, FrameW, FrameH);
                using var scaled = sheet.Clone(ctx => ctx.Crop(crop).Resize(drawW, drawH));
                using var frame = new Image<Rgba32>(targetW, targetH,
                    SixLabors.ImageSharp.Color.Black);
                frame.Mutate(ctx => ctx.DrawImage(scaled,
                    new SixLabors.ImageSharp.Point(drawX, drawY), 1f));
                frame.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delayCs;
                gif.Frames.AddFrame(frame.Frames.RootFrame);
            }
            gif.Frames.RemoveFrame(0); // drop the blank canvas frame

            using var ms = new MemoryStream();
            gif.SaveAsGif(ms);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
