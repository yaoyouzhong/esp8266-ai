using System.Drawing;
using System.Text.Json;

namespace AIClockBridge;

// Weather is fetched by the Windows bridge, never by the clock itself. This
// preserves the USB-first design and makes the last good snapshot available
// when the provider or network is temporarily unreachable.
sealed class WeatherMonitor
{
    public const int HeaderW = 130, HeaderH = 26;
    public const int DateW = 190, DateH = 30;
    public const int AirW = 42, AirH = 30;
    const string CityKey = "weather_city";
    const string AnimationKey = "weather_animation";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static readonly string CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIClockBridge", "weather-cache.json");

    public sealed class Snapshot
    {
        public string City { get; set; } = "";
        public string ConfiguredCity { get; set; } = "";
        public string Condition { get; set; } = "";
        public string AirQuality { get; set; } = "";
        public double Temperature { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Pm25 { get; set; }
        public int Humidity { get; set; }
        public int Icon { get; set; }
        public long EpochUtc { get; set; }
        public int UtcOffsetS { get; set; }
        public long UpdatedUtc { get; set; }
        public bool Stale { get; set; }
    }

    readonly object _lock = new();
    Snapshot _snapshot = new();
    byte[] _headerBitmap = new byte[HeaderW * HeaderH * 2];
    byte[] _dateBitmap = new byte[DateW * DateH * 2];
    byte[] _airBitmap = new byte[AirW * AirH * 2];
    int _dateCenterX = DateW / 2;
    int _headerCenterX = HeaderW / 2;
    int _textRev;
    string _lastText = "";
    System.Threading.Timer _timer;

    public static string City { get => Settings.Get(CityKey); set => Settings.Set(CityKey, value.Trim()); }
    public static string Animation
    {
        get
        {
            var value = Settings.Get(AnimationKey);
            return value is "house" or "plant" or "pet" or "off" ? value : "robot";
        }
        set => Settings.Set(AnimationKey, value is "house" or "plant" or "pet" or "off" ? value : "robot");
    }
    public Snapshot Current { get { lock (_lock) return Clone(_snapshot); } }
    public byte[] HeaderBitmap { get { lock (_lock) return _headerBitmap.ToArray(); } }
    public byte[] DateBitmap { get { lock (_lock) return _dateBitmap.ToArray(); } }
    public byte[] AirBitmap { get { lock (_lock) return _airBitmap.ToArray(); } }
    public int DateCenterX { get { lock (_lock) return _dateCenterX; } }
    public int HeaderCenterX { get { lock (_lock) return _headerCenterX; } }
    public int TextRev { get { lock (_lock) return _textRev; } }

    public void Start()
    {
        LoadCache();
        _ = Refresh();
        _timer = new System.Threading.Timer(_ => _ = Refresh(), null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
    }

    public void SetCity(string city)
    {
        City = city;
        _ = Refresh();
    }

    public byte[] ToJson()
    {
        var value = Current;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            city = value.City, condition = value.Condition, air = value.AirQuality,
            temp = value.Temperature, high = value.High, low = value.Low, pm25 = value.Pm25, humidity = value.Humidity,
            icon = value.Icon, epoch_utc = value.EpochUtc, utc_offset_s = value.UtcOffsetS,
            updated_utc = value.UpdatedUtc, stale = value.Stale, text_rev = TextRev,
            date_center_x = DateCenterX,
            header_center_x = HeaderCenterX,
            animation = Animation,
        });
    }

    public async Task Refresh()
    {
        var city = City;
        if (city.Length == 0) return;
        try
        {
            var geoUrl = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=zh&name=" + Uri.EscapeDataString(city);
            using var geoDoc = JsonDocument.Parse(await Http.GetStringAsync(geoUrl));
            if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) throw new Exception("city not found");
            var result = results[0];
            var lat = result.GetProperty("latitude").GetDouble();
            var lon = result.GetProperty("longitude").GetDouble();
            var canonicalCity = result.TryGetProperty("name", out var name) ? name.GetString() ?? city : city;
            var query = $"latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var weatherUrl = "https://api.open-meteo.com/v1/forecast?" + query
                + "&current=temperature_2m,relative_humidity_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto";
            var airUrl = "https://air-quality-api.open-meteo.com/v1/air-quality?" + query + "&current=us_aqi,pm2_5";
            var weatherTask = Http.GetStringAsync(weatherUrl);
            var airTask = Http.GetStringAsync(airUrl);
            await Task.WhenAll(weatherTask, airTask);
            using var weatherDoc = JsonDocument.Parse(await weatherTask);
            using var airDoc = JsonDocument.Parse(await airTask);
            var current = weatherDoc.RootElement.GetProperty("current");
            var code = current.GetProperty("weather_code").GetInt32();
            var aqi = airDoc.RootElement.TryGetProperty("current", out var air)
                && air.TryGetProperty("us_aqi", out var number) ? number.GetDouble() : double.NaN;
            var pm25 = airDoc.RootElement.TryGetProperty("current", out air)
                && air.TryGetProperty("pm2_5", out number) ? number.GetDouble() : double.NaN;
            var snapshot = new Snapshot
            {
                City = canonicalCity,
                ConfiguredCity = city,
                Condition = ConditionFor(code), AirQuality = AirFor(aqi), Icon = IconFor(code),
                Temperature = current.GetProperty("temperature_2m").GetDouble(),
                Pm25 = double.IsNaN(pm25) ? -1 : pm25,
                Humidity = current.GetProperty("relative_humidity_2m").GetInt32(),
                High = weatherDoc.RootElement.GetProperty("daily").GetProperty("temperature_2m_max")[0].GetDouble(),
                Low = weatherDoc.RootElement.GetProperty("daily").GetProperty("temperature_2m_min")[0].GetDouble(),
                EpochUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                UtcOffsetS = weatherDoc.RootElement.TryGetProperty("utc_offset_seconds", out var offset) ? offset.GetInt32() : 0,
                UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            SetSnapshot(snapshot);
            SaveCache(snapshot);
        }
        catch
        {
            lock (_lock) if (_snapshot.UpdatedUtc > 0) _snapshot.Stale = true;
        }
    }

    void SetSnapshot(Snapshot snapshot)
    {
        snapshot.Stale = false;
        lock (_lock) _snapshot = snapshot;
        RenderText(snapshot);
    }

    void RenderText(Snapshot snapshot)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(snapshot.EpochUtc).ToOffset(TimeSpan.FromSeconds(snapshot.UtcOffsetS));
        var header = $"{snapshot.City} {snapshot.Condition}";
        var date = $"{local.Month}月{local.Day}日 周{Weekday(local.DayOfWeek)}";
        var key = header + "\n" + date + "\n" + snapshot.AirQuality;
        lock (_lock) if (key == _lastText) return;
        using var headerBmp = new Bitmap(HeaderW, HeaderH);
        using var dateBmp = new Bitmap(DateW, DateH);
        using var airBmp = new Bitmap(AirW, AirH);
        DrawChineseText(headerBmp, header, 10f, Color.White, centered: false);
        DrawChineseText(dateBmp, date, 11f, Color.White, centered: false);
        DrawAirBadge(airBmp, snapshot.AirQuality);
        var dateCenterX = VisibleTextCenterX(dateBmp);
        var headerCenterX = VisibleTextCenterX(headerBmp);
        lock (_lock)
        {
            _lastText = key;
            _headerBitmap = Rgb565.Encode(headerBmp);
            _dateBitmap = Rgb565.Encode(dateBmp);
            _airBitmap = Rgb565.Encode(airBmp);
            _dateCenterX = dateCenterX;
            _headerCenterX = headerCenterX;
            _textRev++;
        }
    }

    static int VisibleTextCenterX(Bitmap bitmap)
    {
        var left = bitmap.Width;
        var right = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.R == 0 && pixel.G == 0 && pixel.B == 0) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
        }
        return right >= left ? (left + right + 1) / 2 : bitmap.Width / 2;
    }

    static void DrawChineseText(Bitmap bitmap, string text, float fontSize, Color color, bool centered)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Bold);
        var horizontal = centered ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
        TextRenderer.DrawText(graphics, text, font, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            color, Color.Black, horizontal | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    static void DrawAirBadge(Bitmap bitmap, string air)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        if (string.IsNullOrWhiteSpace(air) || air == "--") return;
        var color = air switch
        {
            "优" => Color.LimeGreen,
            "良" => Color.Gold,
            "轻度" => Color.Orange,
            "中度" => Color.OrangeRed,
            _ => Color.Red,
        };
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Microsoft YaHei UI", air.Length > 1 ? 10f : 15f,
                                  FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        graphics.DrawString(air, font, brush, new RectangleF(0, 0, bitmap.Width, bitmap.Height), format);
    }

    static string Weekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "一", DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三", DayOfWeek.Thursday => "四", DayOfWeek.Friday => "五", _ => "六",
    };

    void LoadCache()
    {
        try
        {
            var cached = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(CachePath));
            var configuredCity = City;
            if (configuredCity.Length > 0 && cached?.UpdatedUtc > 0
                && (string.Equals(cached.ConfiguredCity, configuredCity, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cached.City, configuredCity, StringComparison.OrdinalIgnoreCase)))
            {
                cached.Stale = true;
                lock (_lock) _snapshot = cached;
                RenderText(cached);
            }
        }
        catch { }
    }

    static void SaveCache(Snapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(snapshot));
        }
        catch { }
    }

    static Snapshot Clone(Snapshot value) => new()
    {
        City = value.City, ConfiguredCity = value.ConfiguredCity, Condition = value.Condition, AirQuality = value.AirQuality,
        Temperature = value.Temperature, High = value.High, Low = value.Low, Pm25 = value.Pm25, Humidity = value.Humidity,
        Icon = value.Icon, EpochUtc = value.EpochUtc, UtcOffsetS = value.UtcOffsetS,
        UpdatedUtc = value.UpdatedUtc, Stale = value.Stale,
    };

    static string AirFor(double aqi) => double.IsNaN(aqi) ? "--" : aqi <= 50 ? "优" : aqi <= 100 ? "良" : aqi <= 150 ? "轻度" : aqi <= 200 ? "中度" : "污染";
    static int IconFor(int code) => code == 0 ? 0 : code <= 2 ? 1 : code == 3 ? 2 : code <= 48 ? 3 : code <= 67 || code >= 80 && code <= 82 ? 4 : code <= 77 ? 5 : 6;
    static string ConditionFor(int code) => code switch
    {
        0 => "晴", 1 or 2 => "少云", 3 => "阴", 45 or 48 => "雾",
        >= 51 and <= 57 => "毛毛雨", >= 61 and <= 67 => "小雨",
        >= 71 and <= 77 => "雪", >= 80 and <= 82 => "阵雨", _ => "雷雨",
    };
}
