using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AIClockBridge;

// Tencent is primary and Sina is the independent fallback. The monitor keeps
// its last successful result when both fail, so an outage never blanks the page.
sealed class StockMonitor
{
    public record Row(string Code, string Name, string Price, string Pct, int Up);

    const string SymbolsKey = "stock_symbols";
    public const int MaxRows = 4;
    public const int NameW = 156, NameH = 20;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    static readonly Encoding Gbk;

    readonly object _lock = new();
    Row[] _rows = Array.Empty<Row>();
    byte[] _nameBitmap = new byte[NameW * NameH * MaxRows * 2];
    int _namesRev;
    string _lastNamesKey = "";
    System.Threading.Timer _timer;

    static StockMonitor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { Gbk = Encoding.GetEncoding("GB18030"); }
        catch { Gbk = Encoding.Latin1; }
    }

    public static string[] Symbols
    {
        get
        {
            var raw = Settings.Get(SymbolsKey);
            if (raw.Length == 0) raw = "sh000001";
            return raw.Replace('，', ',').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize).Take(MaxRows).ToArray();
        }
        set => Settings.Set(SymbolsKey, string.Join(",", value.Select(Normalize).Take(MaxRows)));
    }

    public int NamesRev { get { lock (_lock) return _namesRev; } }
    public byte[] NameBitmap { get { lock (_lock) return _nameBitmap.ToArray(); } }
    public Row[] Snapshot { get { lock (_lock) return _rows.ToArray(); } }

    public void Start()
    {
        _ = Fetch();
        _timer = new System.Threading.Timer(_ => _ = Fetch(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public byte[] ToJson()
    {
        var stocks = Snapshot.Select(r => new { code = r.Code, price = r.Price, pct = r.Pct, up = r.Up }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new { stocks, names_rev = NamesRev });
    }

    public void Refresh() => _ = Fetch();

    static string Normalize(string symbol)
    {
        var value = symbol.Trim();
        return value.Length <= 2 ? value.ToLowerInvariant() : value[..2].ToLowerInvariant() + value[2..].ToUpperInvariant();
    }

    async Task Fetch()
    {
        var symbols = Symbols;
        if (symbols.Length == 0) return;
        Row[] rows = Array.Empty<Row>();
        try
        {
            var bytes = await Http.GetByteArrayAsync("http://qt.gtimg.cn/q=" + string.Join(",", symbols));
            rows = ParseTencent(Gbk.GetString(bytes), symbols);
        }
        catch
        {
            // Try the independent source below.
        }
        if (rows.Length == 0) rows = await FetchSina(symbols);
        if (rows.Length == 0) return; // keep the prior, still useful snapshot
        lock (_lock) _rows = rows;
        RenderNamesIfChanged(rows);
    }

    static async Task<Row[]> FetchSina(string[] symbols)
    {
        try
        {
            var query = symbols.Select(SinaSymbol).Where(s => s.Length > 0).ToArray();
            if (query.Length == 0) return Array.Empty<Row>();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://hq.sinajs.cn/list=" + string.Join(",", query));
            request.Headers.Referrer = new Uri("https://finance.sina.com.cn/");
            request.Headers.UserAgent.ParseAdd("AIClockBridge/1.0");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return ParseSina(Gbk.GetString(await response.Content.ReadAsByteArrayAsync()), symbols);
        }
        catch
        {
            return Array.Empty<Row>();
        }
    }

    void RenderNamesIfChanged(Row[] rows)
    {
        var key = string.Join("\n", rows.Select(r => r.Name));
        lock (_lock) if (key == _lastNamesKey) return;
        var packed = new byte[NameW * NameH * MaxRows * 2];
        for (var index = 0; index < rows.Length && index < MaxRows; index++)
        {
            using var bmp = new Bitmap(NameW, NameH);
            using (var graphics = Graphics.FromImage(bmp))
            {
                graphics.Clear(Color.Black);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var font = new Font("Microsoft YaHei UI", 9f);
                TextRenderer.DrawText(graphics, rows[index].Name, font, new Rectangle(0, 0, NameW, NameH),
                    Color.FromArgb(210, 210, 210), Color.Black,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                        | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            Rgb565.Encode(bmp).CopyTo(packed, index * NameW * NameH * 2);
        }
        lock (_lock)
        {
            _lastNamesKey = key;
            _nameBitmap = packed;
            _namesRev++;
        }
    }

    static Row[] ParseTencent(string text, string[] order)
    {
        var bySymbol = new Dictionary<string, Row>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var equal = line.IndexOf('=');
            if (equal < 0 || !line.StartsWith("v_")) continue;
            var symbol = line[2..equal].ToLowerInvariant();
            var f = line[(equal + 1)..].Trim('"', ';', '\r').Split('~');
            if (f.Length <= 32 || !TryNumber(f[3], out var price)
                || !TryNumber(f[31], out var change) || !TryNumber(f[32], out var pct)) continue;
            bySymbol[symbol] = new Row(DisplayCode(symbol), f[1], FormatPrice(price),
                $"{pct:+0.00;-0.00}%", change > 0 ? 1 : change < 0 ? -1 : 0);
        }
        return order.Select(s => s.ToLowerInvariant()).Where(bySymbol.ContainsKey).Select(s => bySymbol[s]).Take(MaxRows).ToArray();
    }

    static Row[] ParseSina(string text, string[] order)
    {
        var bySymbol = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var prefixEnd = line.IndexOf("=\"");
            if (!line.StartsWith("var hq_str_") || prefixEnd < 11) continue;
            var sinaSymbol = line[11..prefixEnd];
            var symbol = FromSinaSymbol(sinaSymbol);
            var f = line[(prefixEnd + 2)..].Trim('"', ';', '\r').Split(',');
            string name;
            double price, change, pct;
            if (symbol.StartsWith("hk", StringComparison.OrdinalIgnoreCase))
            {
                if (f.Length <= 8 || !TryNumber(f[6], out price)
                    || !TryNumber(f[7], out change) || !TryNumber(f[8], out pct)) continue;
                name = f[1];
            }
            else if (symbol.StartsWith("us", StringComparison.OrdinalIgnoreCase))
            {
                if (f.Length <= 4 || !TryNumber(f[1], out price)
                    || !TryNumber(f[4], out change) || !TryNumber(f[2], out pct)) continue;
                name = f[0];
            }
            else
            {
                if (f.Length <= 3 || !TryNumber(f[2], out var previousClose)
                    || !TryNumber(f[3], out price) || previousClose <= 0) continue;
                name = f[0];
                change = price - previousClose;
                pct = change * 100 / previousClose;
            }
            bySymbol[symbol] = new Row(DisplayCode(symbol), name, FormatPrice(price),
                $"{pct:+0.00;-0.00}%", change > 0 ? 1 : change < 0 ? -1 : 0);
        }
        return order.Where(bySymbol.ContainsKey).Select(s => bySymbol[s]).Take(MaxRows).ToArray();
    }

    static string SinaSymbol(string symbol)
    {
        if (symbol.StartsWith("hk", StringComparison.OrdinalIgnoreCase)) return "rt_hk" + symbol[2..];
        if (symbol.StartsWith("us", StringComparison.OrdinalIgnoreCase)) return "gb_" + symbol[2..].ToLowerInvariant();
        return symbol.ToLowerInvariant();
    }

    static string FromSinaSymbol(string symbol)
    {
        if (symbol.StartsWith("rt_hk", StringComparison.OrdinalIgnoreCase)) return "hk" + symbol[5..];
        if (symbol.StartsWith("gb_", StringComparison.OrdinalIgnoreCase)) return "us" + symbol[3..].ToUpperInvariant();
        return Normalize(symbol);
    }

    static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    static string DisplayCode(string symbol)
    {
        foreach (var prefix in new[] { "sh", "sz", "bj", "hk", "us" })
            if (symbol.StartsWith(prefix) && symbol.Length > 2) return symbol[2..].ToUpperInvariant();
        return symbol.ToUpperInvariant();
    }

    static string FormatPrice(double price) => price >= 10_000 ? $"{price:F0}" : price >= 1000 ? $"{price:F1}" : $"{price:F2}";
}
