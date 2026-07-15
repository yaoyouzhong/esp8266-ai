using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AIClockBridge;

// System now-playing bridge for the clock's music page. Uses the public WinRT
// GlobalSystemMediaTransportControlsSessionManager (what SystemMediaTransport
// overlays read), so it sees Spotify, browsers, Groove, etc. If the API is
// unavailable the monitor simply reports idle.
sealed class NowPlayingMonitor
{
    public class MediaSnapshot
    {
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public bool Playing;
        public double Elapsed;
        public double Duration;
        public int ArtworkRev;
        public DateTime UpdatedAt = DateTime.UtcNow;

        public MediaSnapshot Clone() => (MediaSnapshot)MemberwiseClone();
    }

    readonly object _lock = new();
    System.Threading.Timer _timer;
    GlobalSystemMediaTransportControlsSessionManager _manager;
    MediaSnapshot _snapshot = new();
    byte[] _coverRgb565 = Array.Empty<byte>();
    int _lastArtworkHash;
    bool _polling;
    // Pre-rendered title/artist strip for the device. The ESP8266's built-in
    // fonts are ASCII-only, so CJK titles drew as nothing — instead the PC
    // renders the text with real system fonts into a 232x44 RGB565 bitmap
    // that the firmware blits like the cover art. rev bumps on text change.
    byte[] _textRgb565 = Array.Empty<byte>();
    int _textRev;
    string _lastTextKey;

    public const int TextW = 232;
    public const int TextH = 44;
    // The session manager returns a transient empty payload around track
    // changes or system load spikes. Only accept "nothing playing" after
    // several consecutive empties so real metadata isn't wiped by a single
    // hiccup (the visible bug: title/artist randomly disappearing).
    int _emptyStreak;
    const int EmptyStreakToClear = 3;

    const int CoverW = 128;
    const int CoverH = 128;

    public MediaSnapshot Snapshot
    {
        get { lock (_lock) return CurrentLocked(); }
    }

    public byte[] CoverRgb565
    {
        get { lock (_lock) return _coverRgb565; }
    }

    public byte[] TextRgb565
    {
        get { lock (_lock) return _textRgb565; }
    }

    public int TextRev
    {
        get { lock (_lock) return _textRev; }
    }

    public void Start()
    {
        _ = Poll();
        _timer = new System.Threading.Timer(_ => _ = Poll(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public byte[] ToJson()
    {
        RenderTextIfNeeded();
        var s = Snapshot;
        int tRev;
        bool hasArtwork;
        lock (_lock)
        {
            tRev = _textRev;
            hasArtwork = _coverRgb565.Length > 0;
        }
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["title"] = s.Title,
            ["artist"] = s.Artist,
            ["album"] = s.Album,
            ["playing"] = s.Playing,
            ["elapsed"] = (int)Math.Round(s.Elapsed),
            ["duration"] = (int)Math.Round(s.Duration),
            ["progress"] = s.Duration > 0 ? Math.Clamp(s.Elapsed / s.Duration, 0, 1) : 0,
            ["artwork_rev"] = s.ArtworkRev,
            ["has_artwork"] = hasArtwork,
            ["text_rev"] = tRev,
        });
    }

    /// Re-renders the title/artist strip when the strings change. Called on
    /// the /music request path (cheap no-op when nothing changed).
    void RenderTextIfNeeded()
    {
        var s = Snapshot;
        var key = s.Title + "\n" + s.Artist;
        lock (_lock)
        {
            if (key == _lastTextKey) return;
        }
        var rendered = RenderTextStrip(
            title: s.Title.Length == 0 ? "No Music" : s.Title,
            titleColor: s.Title.Length == 0 ? Color.Gray : Color.White,
            artist: s.Artist);
        lock (_lock)
        {
            if (key != _lastTextKey) // re-check under lock (poll may race)
            {
                _lastTextKey = key;
                _textRgb565 = rendered ?? Array.Empty<byte>();
                _textRev++;
            }
        }
    }

    /// 232x44 strip: title (16px semibold, centered, truncated) over artist
    /// (12px, grey). Black background, output big-endian RGB565 like the cover.
    static byte[] RenderTextStrip(string title, Color titleColor, string artist)
    {
        try
        {
            using var bmp = new Bitmap(TextW, TextH,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                };
                using var titleFont = new Font("Microsoft YaHei UI", 16, FontStyle.Bold,
                                               GraphicsUnit.Pixel);
                using var artistFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular,
                                                GraphicsUnit.Pixel);
                using var titleBrush = new SolidBrush(titleColor);
                using var artistBrush = new SolidBrush(Color.FromArgb(184, 184, 184));
                g.DrawString(title, titleFont, titleBrush,
                             new RectangleF(2, 3, TextW - 4, 22), fmt);
                g.DrawString(artist, artistFont, artistBrush,
                             new RectangleF(2, 27, TextW - 4, 16), fmt);
            }
            return Rgb565.Encode(bmp);
        }
        catch
        {
            return null;
        }
    }

    async Task Poll()
    {
        lock (_lock)
        {
            if (_polling) return;
            _polling = true;
        }
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager?.GetCurrentSession();
            if (session == null)
            {
                Apply(new MediaSnapshot(), null);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();

            var playing = playback?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var elapsed = timeline.Position.TotalSeconds;
            var duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
            // Position is a snapshot from LastUpdatedTime; extrapolate while playing
            if (playing && timeline.LastUpdatedTime.Year > 2000)
                elapsed += (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;
            elapsed = duration > 0 ? Math.Clamp(elapsed, 0, duration) : Math.Max(0, elapsed);

            var next = new MediaSnapshot
            {
                Title = props?.Title?.Trim() ?? "",
                Artist = props?.Artist?.Trim() ?? "",
                Album = props?.AlbumTitle?.Trim() ?? "",
                Elapsed = elapsed,
                Duration = Math.Max(0, duration),
                UpdatedAt = DateTime.UtcNow,
            };
            next.Playing = playing && next.Title.Length > 0
                && !StalledAtEnd(next.Elapsed, next.Duration);

            byte[] artworkData = null;
            if (props?.Thumbnail != null)
            {
                try
                {
                    artworkData = await ReadStream(props.Thumbnail);
                }
                catch
                {
                    // some apps hand out broken thumbnail streams
                }
            }
            Apply(next, artworkData);
        }
        catch
        {
            // WinRT unavailable (e.g. very old Windows 10): keep reporting idle
        }
        finally
        {
            lock (_lock) _polling = false;
        }
    }

    static async Task<byte[]> ReadStream(IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        var size = (int)stream.Size;
        if (size <= 0 || size > 10_000_000) return null;
        var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    void Apply(MediaSnapshot next, byte[] artworkData)
    {
        if (ShouldIgnoreAsTransientEmpty(next)) return;

        var artworkHash = ArtworkHash(artworkData);
        var cover = artworkData != null ? MakeCoverRgb565(artworkData, CoverW, CoverH) : null;

        lock (_lock)
        {
            next.ArtworkRev = _snapshot.ArtworkRev;
            if (artworkHash != 0 && artworkHash != _lastArtworkHash && cover != null)
            {
                _coverRgb565 = cover;
                _lastArtworkHash = artworkHash;
                next.ArtworkRev++;
            }
            else if (artworkHash == 0 && _lastArtworkHash != 0)
            {
                _coverRgb565 = Array.Empty<byte>();
                _lastArtworkHash = 0;
                next.ArtworkRev++;
            }
            _snapshot = next;
        }
    }

    static int ArtworkHash(byte[] data)
    {
        if (data == null || data.Length == 0) return 0;
        var hash = new HashCode();
        hash.AddBytes(data);
        var h = hash.ToHashCode();
        return h == 0 ? 1 : h;
    }

    /// True (and swallows the update) when the payload shouldn't replace
    /// what we're showing yet:
    ///  - empty payload (hiccup around track changes): ignored for up to
    ///    3 polls (~6s) while we hold metadata
    ///  - "low quality" payload — title only, no artist/album/duration —
    ///    which is what a web page's media element reports when it briefly
    ///    grabs the system Now Playing slot: ignored for up to 5 polls
    ///    (~10s) while we hold a real song. If it persists longer, it *is*
    ///    what's playing, so it shows through.
    /// Any full-quality payload resets the streak and always applies.
    bool ShouldIgnoreAsTransientEmpty(MediaSnapshot next)
    {
        var isEmpty = next.Title.Length == 0 && next.Artist.Length == 0 && next.Duration <= 0;
        var isGood = next.Title.Length > 0
            && (next.Artist.Length > 0 || next.Album.Length > 0 || next.Duration > 0);
        lock (_lock)
        {
            if (isGood)
            {
                _emptyStreak = 0;
                return false;
            }
            _emptyStreak++;
            var holdingRealSong = _snapshot.Title.Length > 0 && _snapshot.Artist.Length > 0;
            if (isEmpty)
                return _snapshot.Title.Length > 0 && _emptyStreak < EmptyStreakToClear;
            return holdingRealSong && _emptyStreak < 5; // low-quality (web page) source
        }
    }

    MediaSnapshot CurrentLocked()
    {
        var s = _snapshot.Clone();
        if (s.Playing && s.Duration > 0)
        {
            s.Elapsed = Math.Clamp(
                s.Elapsed + (DateTime.UtcNow - s.UpdatedAt).TotalSeconds, 0, s.Duration);
            if (StalledAtEnd(s.Elapsed, s.Duration)) s.Playing = false;
        }
        return s;
    }

    // Some players leave WinRT's playback status at Playing after the final
    // frame. A real track advances instead of remaining pinned at its endpoint.
    static bool StalledAtEnd(double elapsed, double duration) =>
        duration > 0 && elapsed >= duration - 0.5;

    /// Decode artwork bytes, aspect-fill onto a black 128x128, RGB565-encode.
    static byte[] MakeCoverRgb565(byte[] data, int w, int h)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var src = Image.FromStream(ms);
            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var scale = Math.Max((float)w / src.Width, (float)h / src.Height);
                var dw = src.Width * scale;
                var dh = src.Height * scale;
                g.DrawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
            }
            return Rgb565.Encode(bmp);
        }
        catch
        {
            return null;
        }
    }
}
