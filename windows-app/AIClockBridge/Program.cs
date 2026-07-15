using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AIClockBridge;

// Entry point. Runs tray-only (no main window) and starts the /status HTTP
// server that the ESP8266 clock polls — the same endpoints and JSON shapes as
// the Mac app, so the firmware can't tell which OS the bridge runs on.
// Headless smoke test for the petdex -> GIF -> device pipeline (same code the
// pet picker window uses): AIClockBridge --test-pet <slug> <claude|codex> <host>
static class Program
{
    const int Port = 8765;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--quota-auth")
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new DomesticQuotaAuthForm(new DomesticQuotaService(), hideOnUserClose: false));
            return;
        }
        if (args.Length >= 2 && args[0] == "--set-weather-city")
        {
            WeatherMonitor.City = args[1];
            return;
        }
        if (args.Length >= 2 && args[0] == "--startup")
        {
            if (args[1] == "enable") StartupManager.SetEnabled(true);
            else if (args[1] == "disable") StartupManager.SetEnabled(false);
            Console.WriteLine(StartupManager.IsEnabled ? "enabled" : "disabled");
            return;
        }
        if (args.Length >= 2 && args[0] == "--test-pet-download")
        {
            Environment.Exit(TestPetDownload(args[1]).GetAwaiter().GetResult());
            return;
        }
        if (args.Length >= 3 && args[0] == "--test-pet")
        {
            Environment.Exit(TestPet(args).GetAwaiter().GetResult());
            return;
        }
        if (args.Length >= 1 && args[0] == "--test-usb")
        {
            Environment.Exit(TestUsb().GetAwaiter().GetResult());
            return;
        }

        using var singleInstance = new Mutex(true, @"Local\AIClockBridge.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance) return;

        ApplicationConfiguration.Initialize();

        var service = new StatusService();
        var usage = new UsageFetcher();
        service.Usage = usage;
        var domesticUsage = new DomesticQuotaService();
        service.DomesticUsage = domesticUsage;
        var netMonitor = new NetSpeedMonitor();
        netMonitor.Start();
        var nowPlaying = new NowPlayingMonitor();
        nowPlaying.Start();
        service.MusicPlayingProvider = () => nowPlaying.Snapshot.Playing;
        var stocks = new StockMonitor();
        stocks.Start();
        var weather = new WeatherMonitor();
        weather.Start();
        using var serialBridge = new SerialBridge(service, netMonitor, nowPlaying, stocks, weather);
        DeviceClient.Usb = serialBridge;

        var server = new MiniHttpServer(Port,
            routes: new()
            {
                ["/"] = () => service.Snapshot().ToJson(),
                ["/status"] = () => service.Snapshot().ToJson(),
                ["/net"] = () => netMonitor.ToJson(),
                ["/music"] = () => nowPlaying.ToJson(),
                ["/stock"] = () => stocks.ToJson(),
                ["/weather"] = () => weather.ToJson(),
            },
            binaryRoutes: new()
            {
                ["/music/cover.raw"] = () => nowPlaying.CoverRgb565,
                ["/music/text.raw"] = () => nowPlaying.TextRgb565,
                ["/stock/names.raw"] = () => stocks.NameBitmap,
                ["/weather/header.raw"] = () => weather.HeaderBitmap,
                ["/weather/date.raw"] = () => weather.DateBitmap,
                ["/weather/air.raw"] = () => weather.AirBitmap,
            },
            postRoutes: new()
            {
                // Claude Code / Codex hooks push lifecycle events here (README §7):
                // curl -d '{"agent":"claude","event":"PreToolUse"}' http://127.0.0.1:8765/event
                ["/event"] = body =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("agent", out var agent)
                            && root.TryGetProperty("event", out var ev)
                            && agent.ValueKind == JsonValueKind.String
                            && ev.ValueKind == JsonValueKind.String)
                        {
                            string message = null;
                            if (root.TryGetProperty("message", out var msg)
                                && msg.ValueKind == JsonValueKind.String)
                                message = msg.GetString();
                            service.RecordEvent(agent.GetString(), ev.GetString(), message);
                            return Encoding.UTF8.GetBytes("{\"ok\":true}");
                        }
                    }
                    catch
                    {
                        // malformed body
                    }
                    return Encoding.UTF8.GetBytes("{\"ok\":false}");
                },
            });
        // Passive discovery: the clock polls us, so its source IP identifies it.
        // Remember it (for auto-pairing / DHCP-change self-healing) and adopt it
        // outright when no device is configured yet.
        server.OnRequest = (path, ip) =>
        {
            if (path != "/status" && path != "/net" && path != "/music" && path != "/stock" && path != "/weather") return;
            if (ip == "127.0.0.1" || ip == "::1" || ip.Length == 0) return;
            DeviceClient.DevicePollAt = DateTime.UtcNow;
            DeviceClient.LastSeenIp = ip;
            if (DeviceClient.Host.Length == 0) DeviceClient.Host = ip;
        };
        // Active fallback for when the passive route can't fire at all (fresh /
        // erased device knows no bridge host, so it never polls anyone): if the
        // device stays silent, find it ourselves and hand it our address.
        using var pairingWatchdog = new System.Threading.Timer(
            _ => _ = DeviceClient.HealPairingIfNeeded(Port), null,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        try
        {
            server.Start();
            Console.Error.WriteLine($"[bridge] serving /status on 0.0.0.0:{Port}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[bridge] failed to bind port {Port}: {e.Message}");
        }

        var context = new TrayAppContext(service, usage, domesticUsage, netMonitor, nowPlaying, stocks, weather, Port);
        usage.StartAutoRefresh();
        Application.Run(context);
    }

    static async Task<int> TestPet(string[] args)
    {
        var slug = args[1];
        var slot = args[2];
        if (args.Length >= 4) DeviceClient.Host = args[3];
        var (w, h) = slot == "claude" ? (111, 120) : (120, 120);
        var state = PetdexService.States.First(s => s.Id == "running");
        try
        {
            var pets = await PetdexService.LoadManifest();
            var pet = pets.FirstOrDefault(p => p.Slug == slug);
            if (pet == null)
            {
                Console.WriteLine("manifest load failed or slug not found");
                return 1;
            }
            Console.WriteLine($"pet: {pet.DisplayName} {pet.SpritesheetUrl}");
            using var sheet = await PetdexService.DownloadSpritesheet(pet);
            Console.WriteLine($"sheet: {sheet.Width}x{sheet.Height}");
            var gif = PetdexService.BuildGif(sheet, state, w, h);
            if (gif == null)
            {
                Console.WriteLine("gif build failed");
                return 1;
            }
            Console.WriteLine($"gif: {gif.Length} bytes, uploading to {DeviceClient.Host} slot {slot}...");
            await DeviceClient.UploadGif(gif, slot);
            Console.WriteLine("upload ok");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"failed: {e.Message}");
            return 1;
        }
    }

    static async Task<int> TestPetDownload(string slug)
    {
        try
        {
            var pets = await PetdexService.LoadManifest();
            var pet = pets.FirstOrDefault(p => p.Slug == slug);
            if (pet == null) throw new Exception("slug not found");
            using var sheet = await PetdexService.DownloadSpritesheet(pet);
            Console.WriteLine($"{pet.Slug}: {sheet.Width}x{sheet.Height}");
            return sheet.Width > 0 && sheet.Height > 0 ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    static async Task<int> TestUsb()
    {
        ApplicationConfiguration.Initialize();
        var service = new StatusService();
        var net = new NetSpeedMonitor();
        var music = new NowPlayingMonitor();
        var stocks = new StockMonitor();
        stocks.Start();
        var weather = new WeatherMonitor();
        weather.Start();
        using var usb = new SerialBridge(service, net, music, stocks, weather, telemetryEnabled: true);
        DeviceClient.Usb = usb;
        for (var i = 0; i < 200 && !usb.Connected; i++) await Task.Delay(100);
        if (!usb.Connected)
        {
            Console.Error.WriteLine("[test-usb] device handshake timeout");
            return 1;
        }
        try
        {
            for (var i = 0; i < 150 && (stocks.Snapshot.Length == 0 || weather.Current.UpdatedUtc == 0); i++)
                await Task.Delay(100);
            if (stocks.Snapshot.Length == 0) throw new Exception("stock feed did not load");
            if (weather.Current.UpdatedUtc == 0) throw new Exception("weather feed did not load");

            usb.SetDisplayMode("dual");
            await Task.Delay(500);
            usb.RequestInfo();
            for (var i = 0; i < 20 && usb.DeviceInfo?.Effective != "dual"; i++) await Task.Delay(100);
            if (usb.DeviceInfo?.Effective != "dual") throw new Exception("dual quota page did not activate");
            Console.Error.WriteLine("[test-usb] dual quota page verified");

            usb.SetDisplayMode("stock");
            await Task.Delay(3500);
            Console.Error.WriteLine($"[test-usb] stock page {stocks.Snapshot.Length} row(s), names {stocks.NameBitmap.Length} bytes");
            usb.SetDisplayMode("weather");
            await Task.Delay(3500);
            Console.Error.WriteLine($"[test-usb] weather page {weather.Current.City} {weather.Current.Temperature:F0}C, labels {weather.HeaderBitmap.Length + weather.DateBitmap.Length + weather.AirBitmap.Length} bytes");
            usb.RequestInfo();
            for (var i = 0; i < 20 && !(usb.DeviceInfo?.StockUiCached == true && usb.DeviceInfo?.WeatherUiCached == true); i++)
                await Task.Delay(100);
            if (usb.DeviceInfo?.StockUiCached != true || usb.DeviceInfo?.WeatherUiCached != true)
                throw new Exception("device UI cache was not persisted");
            usb.SetDisplayMode("stock");
            await Task.Delay(250);
            usb.SetDisplayMode("weather");
            await Task.Delay(250);
            Console.Error.WriteLine("[test-usb] stock/weather cache switch verified");

            usb.SetDisplayMode("music");
            await Task.Delay(300);
            var cover = new byte[128 * 128 * 2];
            for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
            {
                ushort color = (ushort)(((x & 0xf8) << 8) | ((y & 0xfc) << 3) | ((x + y) >> 4));
                var offset = (y * 128 + x) * 2;
                cover[offset] = (byte)(color >> 8); cover[offset + 1] = (byte)color;
            }
            if (!await usb.SendMusicCoverForTest(cover)) throw new Exception("music cover transfer failed");
            Console.Error.WriteLine($"[test-usb] music cover {cover.Length} bytes verified");
            usb.SetDisplayMode("auto");

            var baseline = await usb.FetchSpriteRaw("claude");
            Console.Error.WriteLine($"[test-usb] baseline sprite {baseline.Length} bytes");

            using var stream = new MemoryStream();
            using (var image = new Image<Rgba32>(111, 120, SixLabors.ImageSharp.Color.Lime))
            using (var second = new Image<Rgba32>(111, 120, SixLabors.ImageSharp.Color.Blue))
            {
                image.Frames.AddFrame(second.Frames.RootFrame);
                image.SaveAsGif(stream);
            }
            var gif = stream.ToArray();
            if (!await usb.UploadGif(gif, "claude"))
                throw new Exception("GIF upload/decode failed");
            var uploaded = await usb.FetchSpriteRaw("claude");
            Console.Error.WriteLine($"[test-usb] uploaded GIF {gif.Length} bytes, sprite {uploaded.Length} bytes");
            if (uploaded.Length < 2 || uploaded[0] < 1) throw new Exception("uploaded sprite invalid");

            usb.ResetSprite("claude");
            await Task.Delay(1000);
            var restored = await usb.FetchSpriteRaw("claude");
            if (!baseline.SequenceEqual(restored)) throw new Exception("default sprite restore mismatch");
            Console.Error.WriteLine("[test-usb] sprite restore verified");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[test-usb] failed: {e.Message}");
            usb.SetDisplayMode("auto");
            usb.ResetSprite("claude");
            return 1;
        }
    }
}
