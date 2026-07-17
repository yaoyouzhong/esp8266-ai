using System.Drawing;
using System.Globalization;
using System.Net;
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
    const string ApiHostKey = "qweather_api_host";
    const string AutoLocationKey = "weather_auto_location";
    const string LatitudeKey = "weather_latitude";
    const string LongitudeKey = "weather_longitude";
    const string CredentialTarget = "AIClockBridge/QWeatherApiKey";
    static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip
                               | DecompressionMethods.Deflate
                               | DecompressionMethods.Brotli,
    }) { Timeout = TimeSpan.FromSeconds(15) };
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
        public string Source { get; set; } = "";
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
    public static string QWeatherApiHost { get => Settings.Get(ApiHostKey); set => Settings.Set(ApiHostKey, NormalizeHost(value)); }
    public static bool AutoLocation { get => Settings.Get(AutoLocationKey) == "1"; set => Settings.Set(AutoLocationKey, value ? "1" : "0"); }
    public static double Latitude => ParseDouble(Settings.Get(LatitudeKey));
    public static double Longitude => ParseDouble(Settings.Get(LongitudeKey));
    public static bool HasQWeatherApiKey => CredentialStore.Read(CredentialTarget).Length > 0;
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

    public void ApplySettings(string host, string city, bool autoLocation,
                              double latitude, double longitude, string apiKey)
    {
        host = NormalizeHost(host);
        ValidateQWeatherHost(host);
        QWeatherApiHost = host;
        City = city;
        AutoLocation = autoLocation;
        if (autoLocation && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180)
        {
            Settings.Set(LatitudeKey, latitude.ToString("F6", CultureInfo.InvariantCulture));
            Settings.Set(LongitudeKey, longitude.ToString("F6", CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(apiKey)) CredentialStore.Write(CredentialTarget, apiKey.Trim());
        _ = Refresh();
    }

    public byte[] ToJson()
    {
        var value = Current;
        // EpochUtc drives the clock on the device. It must be the bridge's
        // current UTC, not the timestamp of the last successful weather fetch.
        // UpdatedUtc intentionally remains unchanged so stale weather can still
        // be identified while its clock keeps running accurately.
        value.EpochUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        RenderText(value);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            city = value.City, condition = value.Condition, air = value.AirQuality,
            temp = value.Temperature, high = value.High, low = value.Low, pm25 = value.Pm25, humidity = value.Humidity,
            icon = value.Icon, epoch_utc = value.EpochUtc, utc_offset_s = value.UtcOffsetS,
            updated_utc = value.UpdatedUtc, stale = value.Stale, text_rev = TextRev,
            date_center_x = DateCenterX,
            header_center_x = HeaderCenterX,
            animation = Animation,
            source = value.Source,
        });
    }

    public async Task Refresh()
    {
        var city = City;
        if (city.Length == 0 && !AutoLocation) return;
        try
        {
            Snapshot snapshot = null;
            var host = QWeatherApiHost;
            var apiKey = CredentialStore.Read(CredentialTarget);
            if (host.Length > 0 && apiKey.Length > 0)
            {
                try { snapshot = await FetchQWeather(host, apiKey, city, AutoLocation, Latitude, Longitude); }
                catch { /* provider fallback below */ }
            }
            snapshot ??= await FetchOpenMeteo(city, AutoLocation, Latitude, Longitude);
            SetSnapshot(snapshot);
            SaveCache(snapshot);
        }
        catch
        {
            lock (_lock) if (_snapshot.UpdatedUtc > 0) _snapshot.Stale = true;
        }
    }

    public async Task<Snapshot> TestQWeather(string host, string apiKey, string city,
                                             bool autoLocation, double latitude, double longitude)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? CredentialStore.Read(CredentialTarget) : apiKey.Trim();
        if (key.Length == 0) throw new InvalidOperationException("请输入 API KEY。 ");
        host = NormalizeHost(host);
        ValidateQWeatherHost(host);
        return await FetchQWeather(host, key, city.Trim(), autoLocation, latitude, longitude);
    }

    static async Task<Snapshot> FetchQWeather(string host, string apiKey, string city,
                                               bool autoLocation, double latitude, double longitude)
    {
        if (host.Length == 0) throw new InvalidOperationException("请输入 API Host。 ");
        var locationQuery = autoLocation
            ? $"{longitude.ToString("F4", CultureInfo.InvariantCulture)},{latitude.ToString("F4", CultureInfo.InvariantCulture)}"
            : city;
        if (locationQuery.Length == 0) throw new InvalidOperationException("请输入地区或使用自动定位。 ");

        using var geo = await QWeatherJson(host, apiKey,
            "/geo/v2/city/lookup?number=1&lang=zh&location=" + Uri.EscapeDataString(locationQuery));
        if (!geo.RootElement.TryGetProperty("code", out var geoCode) || geoCode.GetString() != "200"
            || !geo.RootElement.TryGetProperty("location", out var locations) || locations.GetArrayLength() == 0)
            throw new InvalidOperationException("和风天气没有找到该地区。 ");
        var location = locations[0];
        var id = location.GetProperty("id").GetString() ?? throw new InvalidOperationException("地区缺少 LocationID。 ");
        var resolvedLat = ParseDouble(location.GetProperty("lat").GetString());
        var resolvedLon = ParseDouble(location.GetProperty("lon").GetString());
        var displayCity = QWeatherDisplayCity(location, city);

        var nowTask = QWeatherJson(host, apiKey, $"/v7/weather/now?location={Uri.EscapeDataString(id)}&lang=zh");
        var dailyTask = QWeatherJson(host, apiKey, $"/v7/weather/3d?location={Uri.EscapeDataString(id)}&lang=zh");
        using var nowDoc = await nowTask;
        using var dailyDoc = await dailyTask;
        RequireCode(nowDoc.RootElement, "实时天气");
        RequireCode(dailyDoc.RootElement, "每日预报");
        var now = nowDoc.RootElement.GetProperty("now");
        var daily = dailyDoc.RootElement.GetProperty("daily")[0];
        var aqi = (Value: double.NaN, Category: "");
        var pm25 = -1.0;
        try
        {
            using var airDoc = await QWeatherJson(host, apiKey,
                $"/airquality/v1/current/{resolvedLat.ToString("F2", CultureInfo.InvariantCulture)}/{resolvedLon.ToString("F2", CultureInfo.InvariantCulture)}?lang=zh");
            aqi = AirIndex(airDoc.RootElement);
            pm25 = AirPollutant(airDoc.RootElement, "pm2p5");
        }
        catch
        {
            var fallbackAir = await FetchOpenMeteoAir(resolvedLat, resolvedLon);
            aqi = (fallbackAir.Aqi, "");
            pm25 = fallbackAir.Pm25;
        }
        var updated = DateTimeOffset.TryParse(now.GetProperty("obsTime").GetString(), out var observed)
            ? observed.ToUnixTimeSeconds() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new Snapshot
        {
            City = displayCity, ConfiguredCity = city,
            Condition = CompactCondition(now.GetProperty("text").GetString() ?? "--"),
            AirQuality = CompactAirCategory(aqi.Category, aqi.Value),
            Temperature = ParseDouble(now.GetProperty("temp").GetString()),
            High = ParseDouble(daily.GetProperty("tempMax").GetString()),
            Low = ParseDouble(daily.GetProperty("tempMin").GetString()),
            Pm25 = pm25, Humidity = (int)ParseDouble(now.GetProperty("humidity").GetString()),
            Icon = QWeatherIcon(now.GetProperty("icon").GetString()),
            EpochUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UtcOffsetS = ParseUtcOffset(location.TryGetProperty("utcOffset", out var offset) ? offset.GetString() : null),
            UpdatedUtc = updated, Source = "qweather",
        };
    }

    static string QWeatherDisplayCity(JsonElement location, string fallback)
    {
        var name = location.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "" : "";
        var city = location.TryGetProperty("adm2", out var cityValue) ? cityValue.GetString() ?? "" : "";
        name = CompactPlaceName(name);
        city = CompactPlaceName(city);
        if (name.Length == 0) return CompactPlaceName(fallback);
        if (city.Length == 0 || string.Equals(city, name, StringComparison.OrdinalIgnoreCase)) return name;
        return city + name;
    }

    static string CompactPlaceName(string value)
    {
        value = value.Trim();
        foreach (var suffix in new[] { "特别行政区", "自治州", "地区", "新区", "市", "区", "县" })
            if (value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length)
                return value[..^suffix.Length];
        return value;
    }

    static async Task<Snapshot> FetchOpenMeteo(string city, bool autoLocation, double latitude, double longitude)
    {
        double lat = latitude, lon = longitude;
        var canonicalCity = city;
        if (!autoLocation)
        {
            var geoUrl = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=zh&name=" + Uri.EscapeDataString(city);
            using var geoDoc = JsonDocument.Parse(await Http.GetStringAsync(geoUrl));
            if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                throw new InvalidOperationException("Open-Meteo 没有找到该地区。 ");
            var result = results[0];
            lat = result.GetProperty("latitude").GetDouble();
            lon = result.GetProperty("longitude").GetDouble();
            canonicalCity = result.TryGetProperty("name", out var name) ? name.GetString() ?? city : city;
        }
        var query = $"latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}";
        var weatherTask = Http.GetStringAsync("https://api.open-meteo.com/v1/forecast?" + query
            + "&current=temperature_2m,relative_humidity_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto");
        var airTask = Http.GetStringAsync("https://air-quality-api.open-meteo.com/v1/air-quality?" + query + "&current=us_aqi,pm2_5");
        await Task.WhenAll(weatherTask, airTask);
        using var weatherDoc = JsonDocument.Parse(await weatherTask);
        using var airDoc = JsonDocument.Parse(await airTask);
        var current = weatherDoc.RootElement.GetProperty("current");
        var code = current.GetProperty("weather_code").GetInt32();
        var aqi = airDoc.RootElement.TryGetProperty("current", out var air)
            && air.TryGetProperty("us_aqi", out var number) ? number.GetDouble() : double.NaN;
        var pm25 = airDoc.RootElement.TryGetProperty("current", out air)
            && air.TryGetProperty("pm2_5", out number) ? number.GetDouble() : double.NaN;
        return new Snapshot
        {
            City = canonicalCity, ConfiguredCity = city,
            Condition = ConditionFor(code), AirQuality = AirFor(aqi), Icon = IconFor(code),
            Temperature = current.GetProperty("temperature_2m").GetDouble(),
            Pm25 = double.IsNaN(pm25) ? -1 : pm25,
            Humidity = current.GetProperty("relative_humidity_2m").GetInt32(),
            High = weatherDoc.RootElement.GetProperty("daily").GetProperty("temperature_2m_max")[0].GetDouble(),
            Low = weatherDoc.RootElement.GetProperty("daily").GetProperty("temperature_2m_min")[0].GetDouble(),
            EpochUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UtcOffsetS = weatherDoc.RootElement.TryGetProperty("utc_offset_seconds", out var offset) ? offset.GetInt32() : 0,
            UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Source = "open-meteo",
        };
    }

    static async Task<(double Aqi, double Pm25)> FetchOpenMeteoAir(double latitude, double longitude)
    {
        var query = $"latitude={latitude.ToString(CultureInfo.InvariantCulture)}&longitude={longitude.ToString(CultureInfo.InvariantCulture)}";
        using var document = JsonDocument.Parse(await Http.GetStringAsync(
            "https://air-quality-api.open-meteo.com/v1/air-quality?" + query + "&current=us_aqi,pm2_5"));
        if (!document.RootElement.TryGetProperty("current", out var current)) return (double.NaN, -1);
        var aqi = current.TryGetProperty("us_aqi", out var number) ? number.GetDouble() : double.NaN;
        var pm25 = current.TryGetProperty("pm2_5", out number) ? number.GetDouble() : -1;
        return (aqi, pm25);
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
        using var font = new Font("Microsoft YaHei UI", air.Length > 1 ? 12f : 18f,
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
                if (cached.Condition == "毛毛雨") cached.Condition = "细雨";
                if (string.IsNullOrEmpty(cached.Source)) cached.Source = "open-meteo";
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
        UpdatedUtc = value.UpdatedUtc, Stale = value.Stale, Source = value.Source,
    };

    static async Task<JsonDocument> QWeatherJson(string host, string apiKey, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}{path}");
        request.Headers.Add("X-QW-Api-Key", apiKey);
        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"和风天气请求失败（HTTP {(int)response.StatusCode}）。");
        return JsonDocument.Parse(body);
    }

    static void RequireCode(JsonElement root, string endpoint)
    {
        if (!root.TryGetProperty("code", out var code) || code.GetString() != "200")
            throw new InvalidOperationException($"和风天气{endpoint}返回状态 {code.GetString() ?? "未知"}。 ");
    }

    static (double Value, string Category) AirIndex(JsonElement root)
    {
        if (!root.TryGetProperty("indexes", out var indexes) || indexes.GetArrayLength() == 0)
            return (double.NaN, "");
        JsonElement selected = indexes[0];
        foreach (var item in indexes.EnumerateArray())
        {
            var code = item.TryGetProperty("code", out var value) ? value.GetString() : "";
            if (code == "cn-mee") { selected = item; break; }
            if (code == "qaqi") selected = item;
        }
        var aqi = selected.TryGetProperty("aqi", out var number) && number.TryGetDouble(out var parsed)
            ? parsed : double.NaN;
        var category = selected.TryGetProperty("category", out var text) ? text.GetString() ?? "" : "";
        return (aqi, category);
    }

    static double AirPollutant(JsonElement root, string code)
    {
        if (!root.TryGetProperty("pollutants", out var pollutants)) return -1;
        foreach (var item in pollutants.EnumerateArray())
        {
            if (!item.TryGetProperty("code", out var itemCode) || itemCode.GetString() != code) continue;
            if (item.TryGetProperty("concentration", out var concentration)
                && concentration.TryGetProperty("value", out var value) && value.TryGetDouble(out var parsed))
                return parsed;
        }
        return -1;
    }

    static string CompactAirCategory(string category, double aqi)
    {
        if (category.Contains("优", StringComparison.OrdinalIgnoreCase) || category.Equals("Excellent", StringComparison.OrdinalIgnoreCase)) return "优";
        if (category.Contains("良", StringComparison.OrdinalIgnoreCase) || category.Equals("Good", StringComparison.OrdinalIgnoreCase)) return "良";
        if (category.Contains("轻", StringComparison.OrdinalIgnoreCase)) return "轻度";
        if (category.Contains("中", StringComparison.OrdinalIgnoreCase)) return "中度";
        if (category.Contains("污染", StringComparison.OrdinalIgnoreCase)) return "污染";
        return AirFor(aqi);
    }

    static string CompactCondition(string value)
    {
        if (value == "毛毛雨") return "细雨";
        if (value.Length <= 2) return value;
        if (value.Contains("雷")) return "雷雨";
        if (value.Contains("阵雨")) return "阵雨";
        if (value.Contains("暴雨")) return "暴雨";
        if (value.Contains("大雨")) return "大雨";
        if (value.Contains("中雨")) return "中雨";
        if (value.Contains("雨")) return "小雨";
        if (value.Contains("暴雪")) return "暴雪";
        if (value.Contains("大雪")) return "大雪";
        if (value.Contains("中雪")) return "中雪";
        if (value.Contains("雪")) return "小雪";
        if (value.Contains("霾")) return "霾";
        if (value.Contains("雾")) return "雾";
        if (value.Contains("云")) return "多云";
        return value[..2];
    }

    static int QWeatherIcon(string value)
    {
        if (!int.TryParse(value, out var code)) return 2;
        if (code is 100 or 150) return 0;
        if (code is >= 101 and <= 103 or >= 151 and <= 153) return 1;
        if (code is 104 or 154) return 2;
        if (code is >= 500 and <= 515) return 3;
        if (code is >= 400 and <= 499) return 5;
        if (code is >= 302 and <= 304) return 6;
        if (code is >= 300 and <= 399) return 4;
        return 2;
    }

    static int ParseUtcOffset(string value)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var offset)) return (int)offset.TotalSeconds;
        return (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now).TotalSeconds;
    }

    static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    static string NormalizeHost(string value)
    {
        var host = value.Trim();
        if (Uri.TryCreate(host.Contains("://") ? host : "https://" + host, UriKind.Absolute, out var uri))
            return uri.Host;
        return host.Trim('/');
    }

    static void ValidateQWeatherHost(string host)
    {
        if (host.Length == 0) throw new InvalidOperationException("请输入 API Host。 ");
        if (!host.EndsWith(".qweatherapi.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("API Host 必须是和风天气控制台分配的 qweatherapi.com 域名。 ");
    }

    static string AirFor(double aqi) => double.IsNaN(aqi) ? "--" : aqi <= 50 ? "优" : aqi <= 100 ? "良" : aqi <= 150 ? "轻度" : aqi <= 200 ? "中度" : "污染";
    static int IconFor(int code) => code == 0 ? 0 : code <= 2 ? 1 : code == 3 ? 2 : code <= 48 ? 3 : code <= 67 || code >= 80 && code <= 82 ? 4 : code <= 77 ? 5 : 6;
    static string ConditionFor(int code) => code switch
    {
        0 => "晴", 1 or 2 => "少云", 3 => "阴", 45 or 48 => "雾",
        >= 51 and <= 57 => "细雨", >= 61 and <= 67 => "小雨",
        >= 71 and <= 77 => "雪", >= 80 and <= 82 => "阵雨", _ => "雷雨",
    };
}
