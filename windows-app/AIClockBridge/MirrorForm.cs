using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace AIClockBridge;

// Live "mirror" of the ESP8266 screen, shown as a popup near the tray icon.
// Not a video stream: the PC re-renders the same scene from the same data —
// /api/info says which app the device is showing (and a sprite_rev that bumps
// when animations change), /sprite/<app>/raw provides the exact frames the
// device draws (custom upload or built-in), and the local StatusService
// supplies the quota numbers the device gets from /status. Result: what you
// see here is what the panel shows, including the walk cycle animating only
// while that app is "working".

// MARK: - the 240x240 replica control

sealed class MirrorControl : Control
{
    // scene state, all in the device's 240x240 logical coordinates
    public List<Bitmap> Frames = new();
    public int FrameIdx;
    public int SpriteW = 120, SpriteH = 120;
    public double RingPct;
    public bool NeedsInput; // shown app waiting on approval -> red border flash
    public bool FlashOn;
    public double? FiveHourPct;
    public int? FiveHourResetMin;
    public double? WeeklyPct;
    public int? WeeklyResetMin;
    public int? ResetCreditsAvailable;
    public long? ResetCreditExpiresAt;
    public string Plan = "";
    public bool ShowingClaude = true;
    public bool DeviceOK;
    // net-mode mirror: same scrolling area-chart model as the firmware —
    // one column per 250ms sample, 224-column (56s) window, shared "nice"
    // full-scale, dim-green download area + yellow upload line.
    public bool NetMode;
    public int NetCPU;
    public int NetMem;
    public string NetHeaderDL = "0B";
    public string NetHeaderUL = "0B";
    const int NetCols = 224; // NET_CHART_W
    double[] _histRx = new double[NetCols];
    double[] _histTx = new double[NetCols];

    public bool MusicMode;
    public string MusicTitle = "";
    public string MusicArtist = "";
    public double MusicElapsed;
    public double MusicDuration;
    public bool MusicPlaying;
    public Bitmap MusicCover;

    public bool DomesticMode;
    public DomesticStatus Domestic = new();
    public bool DualMode;
    public StatusSnapshot Dual = new(new ClaudeStatus(), new CodexStatus(), new DomesticStatus(), 0);
    public bool StockMode;
    public StockMonitor.Row[] Stocks = Array.Empty<StockMonitor.Row>();
    public bool WeatherMode;
    public WeatherMonitor.Snapshot Weather = new();
    public bool ScreenSaverMode;

    static readonly Image ClaudeLogo = LoadAsset("claude-logo.png");
    static readonly Image CodexLogo = LoadAsset("codex-logo.png");

    static readonly Color Green = Color.FromArgb(0, 217, 51);
    static readonly Color Yellow = Color.FromArgb(255, 204, 0);
    static readonly Color RingTrack = Color.FromArgb(42, 42, 42);

    public MirrorControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal static Image LoadAsset(string name)
    {
        var asm = typeof(MirrorControl).Assembly;
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        if (resource == null) return new Bitmap(1, 1);
        using var stream = asm.GetManifestResourceStream(resource);
        return Image.FromStream(stream);
    }

    public void ResetNetSweep()
    {
        _histRx = new double[NetCols];
        _histTx = new double[NetCols];
    }

    public void PushNetSample(double rx, double tx)
    {
        Array.Copy(_histRx, 1, _histRx, 0, NetCols - 1);
        _histRx[NetCols - 1] = rx;
        Array.Copy(_histTx, 1, _histTx, 0, NetCols - 1);
        _histTx[NetCols - 1] = tx;
        Invalidate();
    }

    // Keep the visible peak near 87% of chart height at every traffic level.
    static double AdaptiveNetScale(double maxV) => Math.Max(10_240, maxV * 8 / 7);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var scale = Width / 240f;
        g.ScaleTransform(scale, scale);

        // panel background
        using (var panel = RoundedRect(new RectangleF(0, 0, 240, 240), 10))
        {
            g.FillPath(Brushes.Black, panel);
            g.SetClip(panel);
        }

        if (ScreenSaverMode)
        {
            DrawScreenSaverScene(g);
            return;
        }

        if (NetMode)
        {
            DrawNetScene(g);
            return;
        }
        if (MusicMode)
        {
            DrawMusicScene(g);
            return;
        }
        if (DomesticMode)
        {
            DrawDomesticScene(g);
            return;
        }
        if (DualMode)
        {
            DrawDualScene(g);
            return;
        }
        if (StockMode)
        {
            DrawStockScene(g);
            return;
        }
        if (WeatherMode)
        {
            DrawWeatherScene(g);
            return;
        }

        // square quota ring: margin 4, thickness 10, clockwise from top-left
        const float m = 4, t = 10;
        const float side = 240 - 2 * m;
        using (var track = new SolidBrush(RingTrack))
        {
            g.FillRectangle(track, m, m, side, t);
            g.FillRectangle(track, 240 - m - t, m, t, side);
            g.FillRectangle(track, m, 240 - m - t, side, t);
            g.FillRectangle(track, m, m, t, side);
        }
        using (var ring = new SolidBrush(DeviceOK ? Green : Color.FromArgb(90, 90, 90)))
        {
            var remaining = side * 4 * (float)(Math.Clamp(RingPct, 0, 100) / 100);
            const float x0 = m, y0 = m, x1 = 240 - m;
            var seg = Math.Min(remaining, side);
            if (seg > 0) g.FillRectangle(ring, x0, y0, seg, t);                    // top
            remaining -= side;
            seg = Math.Min(remaining, side);
            if (seg > 0) g.FillRectangle(ring, x1 - t, y0, t, seg);                // right
            remaining -= side;
            seg = Math.Min(remaining, side);
            if (seg > 0) g.FillRectangle(ring, x1 - seg, 240 - m - t, seg, t);     // bottom
            remaining -= side;
            seg = Math.Min(remaining, side);
            if (seg > 0) g.FillRectangle(ring, x0, 240 - m - seg, t, seg);         // left
        }

        // sprite, centered, pixel-crisp
        if (Frames.Count > 0)
        {
            var img = Frames[Math.Min(FrameIdx, Frames.Count - 1)];
            var state = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(img, new Rectangle(120 - SpriteW / 2, 120 - SpriteH / 2, SpriteW, SpriteH));
            g.Restore(state);
        }

        // app logo, top-left inside the ring (firmware draws it at 14,18 @40px)
        g.DrawImage(ShowingClaude ? ClaudeLogo : CodexLogo, new Rectangle(14, 18, 40, 40));
        DrawResetCreditBadge(g);
        DrawPlanBadge(g);

        // Window, used percentage and reset countdown share each row, so the
        // existing pet keeps its size and position.
        using (var labelFont = new Font("Consolas", 11, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var valueFont = new Font("Consolas", 14, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var center = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        })
        using (var muted = new SolidBrush(Color.FromArgb(145, 145, 145)))
        using (var panelBrush = new SolidBrush(Color.FromArgb(16, 16, 16)))
        using (var panelPen = new Pen(Color.FromArgb(41, 52, 41)))
        {
            void Panel(RectangleF rect, float radius)
            {
                using var path = RoundedRect(rect, radius);
                g.FillPath(panelBrush, path);
                g.DrawPath(panelPen, path);
            }

            void Row(string label, double? pct, int? reset, float y)
            {
                g.DrawString(label, labelFont, muted, new RectangleF(20, y, 66, 22), center);
                g.DrawString(pct.HasValue ? $"{Math.Clamp((int)pct.Value, 0, 100)}%" : "--",
                    valueFont, Brushes.White, new RectangleF(87, y, 66, 22), center);
                g.DrawString(ResetText(reset), labelFont, Brushes.Cyan,
                    new RectangleF(154, y, 66, 22), center);
            }
            // Match the firmware: Codex never grows a 5H row unless a real
            // account 5H value exists, even while WK is temporarily unknown.
            var singleWeeklyRow = !FiveHourPct.HasValue
                && (!ShowingClaude || WeeklyPct.HasValue);
            if (singleWeeklyRow)
            {
                Panel(new RectangleF(20, 191, 200, 24), 7);
                Row("WK", WeeklyPct, WeeklyResetMin, 192);
            }
            else
            {
                Panel(new RectangleF(20, 178, 200, 21), 6);
                Row("5H", FiveHourPct, FiveHourResetMin, 178);
                Panel(new RectangleF(20, 201, 200, 21), 6);
                Row("WK", WeeklyPct, WeeklyResetMin, 201);
            }
        }

        if (!DeviceOK)
        {
            using var font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center };
            using var red = new SolidBrush(Color.FromArgb(255, 69, 58));
            g.DrawString("设备离线", font, red, new RectangleF(0, 60, 240, 20), fmt);
        }

        // approval pending: blink the whole border red over everything else
        if (NeedsInput && FlashOn)
        {
            using var red = new SolidBrush(Color.FromArgb(255, 59, 48));
            g.FillRectangle(red, m, m, side, t);
            g.FillRectangle(red, m, 240 - m - t, side, t);
            g.FillRectangle(red, m, m, t, side);
            g.FillRectangle(red, 240 - m - t, m, t, side);
        }
    }

    static void DrawScreenSaverScene(Graphics g)
    {
        var now = DateTime.Now;
        var utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var dateFont = new Font("Microsoft YaHei UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
        var weekdays = "日一二三四五六";
        var date = $"{now:MM-dd}";
        var weekday = $"周{weekdays[(int)now.DayOfWeek]}";
        using var textFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
        var dateW = (int)Math.Ceiling(g.MeasureString(date, dateFont, int.MaxValue, textFormat).Width);
        var weekdayW = (int)Math.Ceiling(g.MeasureString(weekday, dateFont, int.MaxValue, textFormat).Width);
        var dateLineW = dateW + 8 + weekdayW;
        const int timeW = 204;
        const int calendarTop = 86;
        var groupW = Math.Max(timeW, dateLineW);
        const int groupH = 112;
        var rangeX = Math.Max(1, 240 - groupW - 12);
        var rangeY = Math.Max(1, 240 - groupH - 24);
        var motionTick = utc / 5;
        var phaseX = (int)((motionTick * 2) % (rangeX * 2L));
        var phaseY = (int)(motionTick % (rangeY * 2L));
        var x = 6 + (phaseX <= rangeX ? phaseX : rangeX * 2 - phaseX);
        var y = 12 + (phaseY <= rangeY ? phaseY : rangeY * 2 - phaseY);
        using var cyan = new SolidBrush(Color.Cyan);
        using var dateBrush = new SolidBrush(Color.FromArgb(198, 203, 198));
        using var accent = new SolidBrush(Color.FromArgb(255, 214, 10));
        var timeX = x + (groupW - timeW) / 2f;
        var digitX = new[] { timeX, timeX + 47, timeX + 115, timeX + 162 };
        var digitValue = new[] { now.Hour / 10, now.Hour % 10, now.Minute / 10, now.Minute % 10 };
        for (var i = 0; i < digitX.Length; i++) DrawLcdDigit(g, digitValue[i], digitX[i], y, cyan);
        g.FillEllipse(accent, timeX + 97, y + 21, 10, 10);
        g.FillEllipse(accent, timeX + 97, y + 45, 10, 10);
        var firstDigitVisibleLeft = now.Hour / 10 == 1 ? 33 : 0;
        var timeVisibleCenter = timeX + (firstDigitVisibleLeft + timeW) / 2f;
        var dateX = timeVisibleCenter - dateLineW / 2f;
        g.DrawString(date, dateFont, dateBrush, dateX, y + calendarTop, textFormat);
        g.DrawString(weekday, dateFont, accent, dateX + dateW + 8, y + calendarTop, textFormat);
    }

    static void DrawLcdDigit(Graphics g, int digit, float x, float y, Brush brush)
    {
        int[] masks = { 0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f };
        const float w = 42, h = 76, t = 9, half = h / 2;
        var mask = masks[Math.Clamp(digit, 0, 9)];
        if ((mask & 0x01) != 0) g.FillRectangle(brush, x + t, y, w - t * 2, t);
        if ((mask & 0x02) != 0) g.FillRectangle(brush, x + w - t, y + t, t, half - t);
        if ((mask & 0x04) != 0) g.FillRectangle(brush, x + w - t, y + half, t, half - t);
        if ((mask & 0x08) != 0) g.FillRectangle(brush, x + t, y + h - t, w - t * 2, t);
        if ((mask & 0x10) != 0) g.FillRectangle(brush, x, y + half, t, half - t);
        if ((mask & 0x20) != 0) g.FillRectangle(brush, x, y + t, t, half - t);
        if ((mask & 0x40) != 0) g.FillRectangle(brush, x + t, y + half - t / 2, w - t * 2, t);
    }

    void DrawPlanBadge(Graphics g)
    {
        if (string.IsNullOrEmpty(Plan)) return;
        var color = PlanColor(Plan);
        using var font = new Font("Consolas", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        var hasResetBadge = !ShowingClaude && ResetCreditsAvailable.HasValue && ResetCreditsAvailable.Value > 0;
        var maxWidth = hasResetBadge ? 88 : 100;
        var width = Math.Clamp((int)Math.Ceiling(g.MeasureString(Plan, font).Width) + 12, 34, maxWidth);
        var rect = new RectangleF(61, 29, width, 18);
        using var path = RoundedRect(rect, 5);
        using var fill = new SolidBrush(Color.FromArgb(35, color));
        using var border = new Pen(color, 1);
        using var text = new SolidBrush(color);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        g.DrawString(Plan, font, text, rect, fmt);
    }

    void DrawResetCreditBadge(Graphics g)
    {
        if (ShowingClaude || !ResetCreditsAvailable.HasValue || ResetCreditsAvailable.Value <= 0) return;
        var color = Green;
        using var countFont = new Font("Consolas", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var dateFont = new Font("Consolas", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        var rect = new RectangleF(153, 29, 68, 18);
        using var path = RoundedRect(rect, 5);
        using var fill = new SolidBrush(Color.FromArgb(35, color));
        using var border = new Pen(color, 1);
        using var countText = new SolidBrush(Green);
        using var dateText = new SolidBrush(color);
        var expiry = ResetCreditDate(ResetCreditExpiresAt);
        var count = $"R*{ResetCreditsAvailable.Value}";
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.NoWrap;
        var countWidth = g.MeasureString(count, countFont, PointF.Empty, fmt).Width;
        var dateWidth = expiry.Length > 0
            ? g.MeasureString(expiry, dateFont, PointF.Empty, fmt).Width : 0;
        var gap = expiry.Length > 0 ? 3f : 0;
        var x = rect.X + (rect.Width - countWidth - gap - dateWidth) / 2;
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        g.DrawString(count, countFont, countText, x,
            rect.Y + (rect.Height - countFont.GetHeight(g)) / 2, fmt);
        if (expiry.Length > 0)
            g.DrawString(expiry, dateFont, dateText, x + countWidth + gap,
                rect.Y + (rect.Height - dateFont.GetHeight(g)) / 2, fmt);
    }

    static Color PlanColor(string plan) => plan switch
    {
        "PRO" or "PRO LITE" => Color.FromArgb(255, 159, 10),
        "PLUS" => Color.FromArgb(10, 210, 255),
        "TEAM" or "BUSINESS" or "ENTERPRISE" => Color.FromArgb(191, 90, 242),
        "MAX" or "MAX 5X" or "MAX 20X" => Color.FromArgb(255, 125, 45),
        _ => Color.FromArgb(174, 174, 178),
    };

    static string ResetCreditDate(long? epoch)
    {
        if (!epoch.HasValue || epoch.Value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return "";
        return DateTimeOffset.FromUnixTimeSeconds(epoch.Value).ToLocalTime().ToString("M/d");
    }

    void DrawDomesticScene(Graphics g)
    {
        var p = Domestic.Active.Model.Length > 0 || Domestic.Active.PlanPct.HasValue
            || Domestic.Active.Balance.HasValue || Domestic.Active.TokensToday > 0
            ? Domestic.Active
            : Domestic.ActiveProvider switch
            {
                "xiaomi" => Domestic.Xiaomi,
                "kimi" => Domestic.Kimi,
                "minimax" => Domestic.MiniMax,
                "deepseek" => Domestic.DeepSeek,
                _ => Domestic.Qwen,
            };
        var provider = string.IsNullOrEmpty(Domestic.ActiveProvider)
            ? "QWEN" : Domestic.ActiveProvider.ToUpperInvariant();
        var isKimi = provider == "KIMI";
        var isBalance = provider == "DEEPSEEK" && p.Balance.HasValue;
        var isWindowed = isKimi || p.FiveHourPct.HasValue || p.WeeklyPct.HasValue;
        var displayPct = isWindowed ? p.WeeklyPct ?? p.PlanPct : p.PlanPct;
        var plan = isBalance ? 0 : displayPct.HasValue ? Math.Clamp((int)displayPct.Value, 0, 100) : 0;
        var planNumber = isBalance
            ? p.Balance.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : displayPct.HasValue
            ? (!isWindowed && p.PlanPctText.Length > 0 ? p.PlanPctText
                : ((int)Math.Clamp(displayPct.Value, 0, 100)).ToString(CultureInfo.InvariantCulture))
            : "--";
        var remaining = p.PlanPct.HasValue
            ? $"{(p.RemainingPctText.Length > 0 ? p.RemainingPctText
                : (Math.Truncate(Math.Max(0, 100 - p.PlanPct.Value) * 100) / 100)
                    .ToString("0.00", CultureInfo.InvariantCulture))}% LEFT"
            : "QUOTA UNKNOWN";
        var panelColor = Color.FromArgb(16, 16, 16);
        var mutedColor = Color.FromArgb(123, 125, 123);
        var numberColor = Color.FromArgb(255, 251, 222);

        DrawDomesticRing(g, plan);

        using var providerFont = new Font("Consolas", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        using var modelFont = new Font("Consolas", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var membershipFont = new Font("Consolas", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Consolas", 9, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelFont = new Font("Consolas", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        var percentSize = planNumber.Length <= 3 ? 54 : planNumber.Length <= 6 ? 40
            : planNumber.Length <= 9 ? 30 : 24;
        using var percentFont = new Font("Consolas", percentSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var suffixFont = new Font("Consolas", percentSize <= 30 ? 14 : 22,
            FontStyle.Bold, GraphicsUnit.Pixel);
        using var resetValueFont = new Font("Consolas", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var greenBrush = new SolidBrush(Green);
        using var mutedBrush = new SolidBrush(mutedColor);
        using var numberBrush = new SolidBrush(numberColor);
        using var cyanBrush = new SolidBrush(Color.Cyan);
        using var panelBrush = new SolidBrush(panelColor);
        using var panelPen = new Pen(Color.FromArgb(41, 52, 41));
        using var centered = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        g.FillEllipse(greenBrush, 21, 26, 8, 8);
        g.DrawString(provider, providerFont, greenBrush, 36, 22);
        if (p.MembershipBadge && !string.IsNullOrEmpty(p.Model))
        {
            var badgeColor = Color.FromArgb(255, 159, 10);
            var badgeWidth = Math.Clamp((int)Math.Ceiling(g.MeasureString(p.Model, membershipFont).Width) + 12,
                                        34, 112);
            var badgeRect = new RectangleF(218 - badgeWidth, 22, badgeWidth, 18);
            using var badgePath = RoundedRect(badgeRect, 5);
            using var badgeFill = new SolidBrush(Color.FromArgb(35, badgeColor));
            using var badgeBorder = new Pen(badgeColor, 1);
            using var badgeText = new SolidBrush(badgeColor);
            g.FillPath(badgeFill, badgePath);
            g.DrawPath(badgeBorder, badgePath);
            g.DrawString(p.Model, membershipFont, badgeText, badgeRect, centered);
        }
        else
        {
            using var right = new StringFormat(centered) { Alignment = StringAlignment.Far };
            g.DrawString(string.IsNullOrEmpty(p.Model) ? "--" : p.Model, modelFont, Brushes.LightGray,
                         new RectangleF(106, 20, 112, 22), right);
        }
        g.FillRectangle(mutedBrush, 20, 53, 200, 1);

        if (isBalance)
        {
            DrawDeepSeekBalance(g, planNumber, p.Currency, mutedBrush, numberBrush, greenBrush);
        }
        else
        {
            g.DrawString(isWindowed ? "WEEKLY" : "PLAN", smallFont, mutedBrush,
                         new RectangleF(0, 69, 240, 16), centered);
            var numberSize = g.MeasureString(planNumber, percentFont);
            var suffixWidth = p.PlanPct.HasValue ? g.MeasureString("%", suffixFont).Width + 4 : 0;
            var numberLeft = 120 - (numberSize.Width + suffixWidth) / 2;
            var numberTop = 90 + (54 - numberSize.Height) / 2;
            g.DrawString(planNumber, percentFont, numberBrush, numberLeft, numberTop);
            if (p.PlanPct.HasValue)
                g.DrawString("%", suffixFont, greenBrush, numberLeft + numberSize.Width + 4,
                    numberTop + numberSize.Height - suffixFont.Height - 1);
        }

        if (!isBalance)
        {
            const float resetBaseline = 164;
            using var resetFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
            resetFormat.FormatFlags |= StringFormatFlags.NoWrap;
            var smallAscent = smallFont.Size * smallFont.FontFamily.GetCellAscent(smallFont.Style)
                / smallFont.FontFamily.GetEmHeight(smallFont.Style);
            var valueAscent = labelFont.Size * labelFont.FontFamily.GetCellAscent(labelFont.Style)
                / labelFont.FontFamily.GetEmHeight(labelFont.Style);
            g.DrawString(isWindowed ? "RESET" : "REMAINING", smallFont, mutedBrush,
                new PointF(37, resetBaseline - smallAscent), resetFormat);
            resetFormat.Alignment = StringAlignment.Far;
            g.DrawString(isWindowed ? ResetText(p.WeeklyResetMin) : remaining, labelFont, greenBrush,
                new PointF(203, resetBaseline - valueAscent), resetFormat);
        }

        using (var panel = RoundedRect(new RectangleF(20, 177, 200, 38), 8))
            g.FillPath(panelBrush, panel);
        using (var panel = RoundedRect(new RectangleF(20, 177, 200, 38), 8))
            g.DrawPath(panelPen, panel);
        var panelValue = isWindowed
            ? (p.FiveHourPct.HasValue
                ? $"{(int)Math.Clamp(p.FiveHourPct.Value, 0, 100)}%" : "--")
            : PlanResetText(p.PlanResetAt, p.PlanResetMin);
        if (isBalance)
        {
            g.DrawString("USED", labelFont, greenBrush,
                new RectangleF(20, 177, 66, 38), centered);
            if (p.UsedCost.HasValue)
            {
                var amount = p.UsedCost.Value.ToString("0.00", CultureInfo.InvariantCulture);
                var amountWidth = g.MeasureString(amount, resetValueFont).Width;
                var currencyWidth = string.IsNullOrEmpty(p.Currency)
                    ? 0 : g.MeasureString(p.Currency, resetValueFont).Width;
                var gap = currencyWidth > 0 ? 5 : 0;
                var left = 153.5f - (amountWidth + gap + currencyWidth) / 2;
                g.DrawString(amount, resetValueFont, numberBrush,
                    new RectangleF(left, 177, amountWidth, 38), centered);
                if (currencyWidth > 0)
                    g.DrawString(p.Currency, resetValueFont, greenBrush,
                        new RectangleF(left + amountWidth + gap, 177, currencyWidth, 38), centered);
            }
            else
            {
                g.DrawString("--", resetValueFont, numberBrush,
                    new RectangleF(87, 177, 133, 38), centered);
            }
        }
        else if (isWindowed)
        {
            g.DrawString("5H", labelFont, greenBrush,
                new RectangleF(20, 177, 66, 38), centered);
            g.DrawString(panelValue, labelFont, Brushes.White,
                new RectangleF(87, 177, 66, 38), centered);
            g.DrawString(ResetText(p.FiveHourResetMin), labelFont, cyanBrush,
                new RectangleF(154, 177, 66, 38), centered);
        }
        else
        {
            g.DrawString("RESET", labelFont, greenBrush,
                new RectangleF(20, 177, 66, 38), centered);
            g.DrawString(panelValue, resetValueFont, cyanBrush,
                new RectangleF(87, 177, 133, 38), centered);
        }
    }

    static void DrawDeepSeekBalance(Graphics g, string balance, string currency,
        Brush captionBrush, Brush numberBrush, Brush currencyBrush)
    {
        const string caption = "AVAILABLE BALANCE";
        var area = new RectangleF(20, 62, 200, 106);
        const float maxContentWidth = 184;
        const float maxContentHeight = 90;
        using var typographic = (StringFormat)StringFormat.GenericTypographic.Clone();
        typographic.FormatFlags |= StringFormatFlags.NoWrap;

        float valueSize = 48;
        float captionSize = 16;
        float currencySize = 26;
        float lineGap = 8;
        float valueGap = string.IsNullOrEmpty(currency) ? 0 : 6;
        for (; valueSize >= 20; valueSize -= 1)
        {
            captionSize = Math.Clamp((float)Math.Round(valueSize * 0.34f), 10, 16);
            currencySize = Math.Clamp((float)Math.Round(valueSize * 0.55f), 12, 26);
            lineGap = Math.Clamp((float)Math.Round(valueSize * 0.17f), 5, 8);
            valueGap = string.IsNullOrEmpty(currency) ? 0
                : Math.Clamp((float)Math.Round(valueSize * 0.13f), 4, 6);
            using var testCaptionFont = new Font("Consolas", captionSize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var testValueFont = new Font("Consolas", valueSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var testCurrencyFont = new Font("Consolas", currencySize, FontStyle.Bold, GraphicsUnit.Pixel);
            var captionWidth = g.MeasureString(caption, testCaptionFont, PointF.Empty, typographic).Width;
            var numberWidth = g.MeasureString(balance, testValueFont, PointF.Empty, typographic).Width;
            var currencyWidth = string.IsNullOrEmpty(currency) ? 0
                : g.MeasureString(currency, testCurrencyFont, PointF.Empty, typographic).Width;
            var totalHeight = testCaptionFont.GetHeight(g) + lineGap + testValueFont.GetHeight(g);
            if (Math.Max(captionWidth, numberWidth + valueGap + currencyWidth) <= maxContentWidth
                && totalHeight <= maxContentHeight)
                break;
        }

        using var captionFont = new Font("Consolas", captionSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Consolas", valueSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var currencyFont = new Font("Consolas", currencySize, FontStyle.Bold, GraphicsUnit.Pixel);
        var captionMeasured = g.MeasureString(caption, captionFont, PointF.Empty, typographic);
        var numberMeasured = g.MeasureString(balance, valueFont, PointF.Empty, typographic);
        var currencyMeasured = string.IsNullOrEmpty(currency) ? SizeF.Empty
            : g.MeasureString(currency, currencyFont, PointF.Empty, typographic);
        var valueWidth = numberMeasured.Width + valueGap + currencyMeasured.Width;
        var componentHeight = captionFont.GetHeight(g) + lineGap + valueFont.GetHeight(g);
        var centerX = area.Left + area.Width / 2;
        var top = area.Top + (area.Height - componentHeight) / 2;
        var valueTop = top + captionFont.GetHeight(g) + lineGap;
        var valueLeft = centerX - valueWidth / 2;

        g.DrawString(caption, captionFont, captionBrush,
            new PointF(centerX - captionMeasured.Width / 2, top), typographic);
        g.DrawString(balance, valueFont, numberBrush, new PointF(valueLeft, valueTop), typographic);
        if (!string.IsNullOrEmpty(currency))
        {
            var valueAscent = valueFont.Size * valueFont.FontFamily.GetCellAscent(valueFont.Style)
                / valueFont.FontFamily.GetEmHeight(valueFont.Style);
            var currencyAscent = currencyFont.Size * currencyFont.FontFamily.GetCellAscent(currencyFont.Style)
                / currencyFont.FontFamily.GetEmHeight(currencyFont.Style);
            g.DrawString(currency, currencyFont, currencyBrush,
                new PointF(valueLeft + numberMeasured.Width + valueGap,
                    valueTop + valueAscent - currencyAscent), typographic);
        }
    }

    static string PlanResetText(long? epoch, int? minutes)
    {
        if (epoch.HasValue && epoch.Value > 0)
            return DateTimeOffset.FromUnixTimeSeconds(epoch.Value).ToLocalTime().ToString("MM-dd HH:mm");
        return ResetText(minutes);
    }

    void DrawDualScene(Graphics g)
    {
        using var titleFont = new Font("Consolas", 9f, FontStyle.Bold);
        using var appFont = new Font("Consolas", 10f, FontStyle.Bold);
        using var labelFont = new Font("Consolas", 7.5f, FontStyle.Bold);
        using var valueFont = new Font("Consolas", 12f, FontStyle.Bold);
        using var smallFont = new Font("Consolas", 6.5f);
        using var muted = new SolidBrush(Color.FromArgb(145, 145, 145));
        using var center = new StringFormat { Alignment = StringAlignment.Center };
        using var right = new StringFormat { Alignment = StringAlignment.Far };

        g.DrawString("USAGE OVERVIEW", titleFont, Brushes.White,
            new RectangleF(0, 8, 240, 18), center);
        using (var divider = new Pen(Color.FromArgb(45, 45, 45)))
            g.DrawLine(divider, 18, 121, 222, 121);

        void Section(string name, string plan, string status, double? firstPct, int? firstReset,
                     double? weeklyPct, int? weeklyReset, float top, bool collapseFirst,
                     int? resetCredits = null)
        {
            var statusColor = status == "working" ? Green
                : status == "idle" ? Yellow : Color.FromArgb(90, 90, 90);
            using var statusBrush = new SolidBrush(statusColor);
            g.FillEllipse(statusBrush, 18, top + 4, 7, 7);
            g.DrawString(name, appFont, name == "CLAUDE" ? Brushes.Orange : Brushes.Cyan, 31, top);
            if (resetCredits.HasValue && resetCredits.Value > 0)
            {
                var color = Green;
                var badge = new RectangleF(81, top, 34, 17);
                using var badgeText = new SolidBrush(color);
                using var badgeFormat = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.DrawString($"R*{resetCredits.Value}", appFont, badgeText, badge, badgeFormat);
            }
            if (!string.IsNullOrWhiteSpace(plan))
            {
                var color = PlanColor(plan);
                var badgeWidth = Math.Clamp((int)Math.Ceiling(g.MeasureString(plan, smallFont).Width) + 16, 40, 112);
                var badge = new RectangleF(220 - badgeWidth, top, badgeWidth, 16);
                using var badgePath = RoundedRect(badge, 4);
                using var badgeFill = new SolidBrush(Color.FromArgb(35, color));
                using var badgeBorder = new Pen(color, 1);
                using var badgeText = new SolidBrush(color);
                using var badgeFormat = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.FillPath(badgeFill, badgePath);
                g.DrawPath(badgeBorder, badgePath);
                g.DrawString(plan, smallFont, badgeText, badge, badgeFormat);
            }

            void Row(string label, double? pct, int? reset, float y)
            {
                g.DrawString(label, labelFont, muted, 20, y + 3);
                g.DrawString(ResetText(reset), smallFont, muted, 52, y + 5);
                var value = pct.HasValue ? $"{Math.Round(pct.Value):0}%" : "--";
                g.DrawString(value, valueFont, Brushes.White, new RectangleF(150, y, 70, 20), right);
                var bar = new RectangleF(20, y + 23, 200, 6);
                using var track = new SolidBrush(Color.FromArgb(42, 42, 42));
                g.FillRectangle(track, bar);
                if (pct.HasValue)
                {
                    var p = Math.Clamp(pct.Value, 0, 100);
                    var color = p >= 99.5 ? Color.Red : p >= 80 ? Yellow : Green;
                    using var fill = new SolidBrush(color);
                    g.FillRectangle(fill, bar.X, bar.Y, bar.Width * (float)(p / 100), bar.Height);
                }
            }

            if (collapseFirst) Row("WK", weeklyPct, weeklyReset, top + 35);
            else
            {
                Row("5H", firstPct, firstReset, top + 24);
                Row("WK", weeklyPct, weeklyReset, top + 57);
            }
        }

        Section("CLAUDE", Dual.Claude.Plan, Dual.Claude.Status,
            Dual.Claude.FiveHourPct, Dual.Claude.FiveHourResetMin,
            Dual.Claude.SevenDayPct, Dual.Claude.SevenDayResetMin, 31, false);
        var codexSingle = !Dual.Codex.PrimaryPct.HasValue;
        Section("CODEX", Dual.Codex.Plan, Dual.Codex.Status,
            Dual.Codex.PrimaryPct, Dual.Codex.PrimaryResetMin,
            Dual.Codex.WeeklyPct, Dual.Codex.WeeklyResetMin, 132, codexSingle,
            Dual.Codex.ResetCreditsAvailable);
    }

    static string ResetText(int? minutes)
    {
        if (!minutes.HasValue || minutes.Value < 0) return "";
        var m = minutes.Value;
        if (m >= 1440) return $"{m / 1440}d {m % 1440 / 60}h";
        if (m >= 60) return $"{m / 60}h {m % 60}m";
        return $"{m}m";
    }

    static void DrawDomesticRing(Graphics g, double pct)
    {
        const float margin = 4, thickness = 10, side = 232;
        var remaining = side * 4 * (float)(Math.Clamp(pct, 0, 100) / 100);
        using var brush = new SolidBrush(Green);
        var segment = Math.Min(remaining, side);
        if (segment > 0) g.FillRectangle(brush, margin, margin, segment, thickness);
        remaining -= side;
        segment = Math.Min(Math.Max(remaining, 0), side);
        if (segment > 0) g.FillRectangle(brush, 226, margin, thickness, segment);
        remaining -= side;
        segment = Math.Min(Math.Max(remaining, 0), side);
        if (segment > 0) g.FillRectangle(brush, 236 - segment, 226, segment, thickness);
        remaining -= side;
        segment = Math.Min(Math.Max(remaining, 0), side);
        if (segment > 0) g.FillRectangle(brush, margin, 236 - segment, thickness, segment);
    }

    static string PctText(double? pct) => pct.HasValue ? $"{(int)pct.Value}%" : "--";

    static string TokenText(long tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F1}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return tokens.ToString();
    }

    void DrawStockScene(Graphics g)
    {
        using var codeFont = new Font("Consolas", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var nameFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Consolas", 24, FontStyle.Regular, GraphicsUnit.Pixel);
        using var footerFont = new Font("Consolas", 8, FontStyle.Regular, GraphicsUnit.Pixel);
        using var right = new StringFormat { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter };
        var pageCount = Math.Max(1, (Stocks.Length + StockMonitor.RowsPerPage - 1) / StockMonitor.RowsPerPage);
        var page = pageCount == 1 ? 0 : (int)((Environment.TickCount64 / StockMonitor.PageIntervalMs) % pageCount);
        var pageStart = page * StockMonitor.RowsPerPage;
        for (var i = 0; i < Math.Min(StockMonitor.RowsPerPage, Stocks.Length - pageStart); i++)
        {
            var row = Stocks[pageStart + i];
            var y = 6 + i * 54;
            g.DrawString(row.Code, codeFont, Brushes.Gray, 14, y);
            g.DrawString(row.Name, nameFont, Brushes.LightGray, new RectangleF(70, y, 156, 20), right);
            g.DrawString(row.Price, valueFont, Brushes.White, 14, y + 20);
            using var change = new SolidBrush(row.Up > 0 ? Color.Red : row.Up < 0 ? Green : Color.LightGray);
            g.DrawString(row.Pct, valueFont, change, new RectangleF(130, y + 20, 96, 31), right);
        }
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        var footer = Stocks.Length == 0 ? "Waiting for bridge..."
            : pageCount > 1 ? $"STOCKS {page + 1}/{pageCount}" : "STOCKS";
        g.DrawString(footer, footerFont,
                     Brushes.Gray, new RectangleF(0, Stocks.Length == 0 ? 104 : 228, 240, 12), centered);
    }

    void DrawWeatherScene(Graphics g)
    {
        var w = Weather;
        using var headerFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
        headerFormat.FormatFlags |= StringFormatFlags.NoWrap;
        var headerFontSize = 20f;
        SizeF citySize = SizeF.Empty;
        for (; headerFontSize > 10f; headerFontSize -= 1f)
        {
            using var candidate = new Font("Microsoft YaHei UI", headerFontSize,
                FontStyle.Bold, GraphicsUnit.Pixel);
            citySize = g.MeasureString(w.City, candidate, int.MaxValue, headerFormat);
            if (Math.Ceiling(citySize.Width) <= 120) break;
        }
        using var headerFont = new Font("Microsoft YaHei UI", headerFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var rangeFont = new Font("Consolas", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using var timeFont = new Font("Consolas", 48, FontStyle.Bold, GraphicsUnit.Pixel);
        using var secondFont = new Font("Consolas", 25, FontStyle.Regular, GraphicsUnit.Pixel);
        using var dateFont = new Font("Microsoft YaHei UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
        using var metricFont = new Font("Consolas", 24, FontStyle.Regular, GraphicsUnit.Pixel);
        citySize = g.MeasureString(w.City, headerFont, int.MaxValue, headerFormat);
        var cityY = 1 + Math.Max(0, (26 - citySize.Height) / 2f);
        g.DrawString(w.City, headerFont, Brushes.White, 14, cityY, headerFormat);
        var rangeY = headerFontSize < 17 ? 33 : 34;
        g.DrawString($"L {(int)Math.Round(w.Low)}C", rangeFont, Brushes.Cyan, 20, rangeY);
        g.DrawString($"H {(int)Math.Round(w.High)}C", rangeFont, Brushes.Orange, 81, rangeY);
        if (!string.IsNullOrWhiteSpace(w.AirQuality) && w.AirQuality != "--")
        {
            var airColor = WeatherMonitor.AirQualityColor(w.AirQuality);
            var airBadgeRect = w.AirQuality.Length > 1
                ? new RectangleF(137.5f, 15f, 39f, 24f)
                : new RectangleF(144.5f, 14.5f, 25f, 25f);
            using (var badgePath = RoundedRect(airBadgeRect, w.AirQuality.Length > 1 ? 8f : 12.5f))
            using (var badgeFill = new SolidBrush(WeatherMonitor.BadgeBackground(airColor)))
            using (var badgeBorder = new Pen(airColor, 1.4f))
            {
                g.FillPath(badgeFill, badgePath);
                g.DrawPath(badgeBorder, badgePath);
            }
            using var badgeFont = new Font("Microsoft YaHei UI", w.AirQuality.Length > 1 ? 12 : 18,
                FontStyle.Bold, GraphicsUnit.Pixel);
            using var badgeBrush = new SolidBrush(airColor);
            using var badgeFormat = new StringFormat
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(w.AirQuality, badgeFont, badgeBrush,
                new RectangleF(136, 12, 42, 30), badgeFormat);
        }
        using (var conditionFont = new Font("Microsoft YaHei UI", w.Condition.Length > 1 ? 19 : 22,
                   FontStyle.Bold, GraphicsUnit.Pixel))
        using (var conditionBrush = new SolidBrush(WeatherMonitor.ConditionColor(w.Condition)))
        using (var conditionFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(w.Condition, conditionFont, conditionBrush,
                new RectangleF(184, 12, 52, 30), conditionFormat);

        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromSeconds(w.UtcOffsetS));
        var hm = now.ToString("HHmm");
        g.DrawString(hm[..2], timeFont, Brushes.White, 17, 54);
        using var yellowBrush = new SolidBrush(Yellow);
        g.DrawString(hm[2..], timeFont, yellowBrush, 101, 54);
        g.DrawString(now.ToString("ss"), secondFont, Brushes.White, 190, 80);
        var weekdays = "日一二三四五六";
        g.DrawString($"{now.Month}月{now.Day}日 周{weekdays[(int)now.DayOfWeek]}", dateFont, Brushes.White, 14, 117);
        g.DrawString($"TEMP   {(int)Math.Round(w.Temperature)}C", metricFont, Brushes.White, 14, 161);
        g.DrawString($"HUMID  {w.Humidity}%", metricFont, Brushes.White, 14, 198);
    }

    void DrawMusicScene(Graphics g)
    {
        var coverRect = new Rectangle(56, 16, 128, 128);
        if (MusicCover != null)
        {
            var state = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(MusicCover, coverRect);
            g.Restore(state);
        }
        else
        {
            using var dark = new SolidBrush(Color.FromArgb(64, 64, 64));
            g.FillRectangle(dark, coverRect);
            using var font = new Font("Consolas", 13, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("No Art", font, Brushes.LightGray, new RectangleF(56, 72, 128, 20), fmt);
        }

        using var titleFmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        var title = MusicTitle.Length == 0 ? "No Music" : MusicTitle;
        using (var font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            g.DrawString(title, font, Brushes.White, new RectangleF(12, 154, 216, 24), titleFmt);
        }
        using (var font = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            g.DrawString(MusicArtist, font, Brushes.LightGray,
                         new RectangleF(12, 178, 216, 20), titleFmt);
        }

        var bar = new RectangleF(20, 210, 200, 8);
        using var barBg = new SolidBrush(Color.FromArgb(64, 64, 64));
        g.FillRectangle(barBg, bar);
        var frac = MusicDuration > 0 ? (float)Math.Clamp(MusicElapsed / MusicDuration, 0, 1) : 0;
        using var barFill = new SolidBrush(MusicPlaying ? Green : Color.Gray);
        g.FillRectangle(barFill, bar.X, bar.Y, bar.Width * frac, bar.Height);
    }

    /// Replica of the firmware's net-speed screen v2: header readouts, then
    /// a 224x128 area chart at (8,60) — dim-green DL fill with bright top
    /// edge, 2px yellow UL line, quarter gridlines, shared nice scale.
    void DrawNetScene(Graphics g)
    {
        var grey = Color.FromArgb(140, 140, 140);
        using var greyBrush = new SolidBrush(grey);
        using var labelFont = new Font("Consolas", 8, FontStyle.Regular, GraphicsUnit.Pixel);

        g.DrawString("DOWN", labelFont, greyBrush, 14, 8);
        g.DrawString("UP", labelFont, greyBrush, 134, 8);
        using (var valueFont = new Font("Consolas", 19, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var greenBrush = new SolidBrush(Green))
        using (var yellowBrush = new SolidBrush(Yellow))
        {
            g.DrawString(NetHeaderDL + "/s", valueFont, greenBrush, 12, 19);
            g.DrawString(NetHeaderUL + "/s", valueFont, yellowBrush, 132, 19);
        }

        const float cx = 8, cy = 60, cw = 224, ch = 128;
        var scale = AdaptiveNetScale(Math.Max(_histRx.Max(), _histTx.Max()));

        // quarter gridlines
        using (var grid = new Pen(Color.FromArgb(41, 41, 41), 1))
        {
            for (int q = 1; q <= 3; q++)
            {
                var y = cy + ch * q / 4;
                g.DrawLine(grid, cx, y, cx + cw, y);
            }
        }

        // 3-tap smoothed points, one per column (matches the device)
        PointF[] Points(double[] vals)
        {
            var pts = new PointF[NetCols];
            for (int i = 0; i < NetCols; i++)
            {
                var lo = Math.Max(0, i - 1);
                var hi = Math.Min(NetCols - 1, i + 1);
                var v = (vals[lo] + vals[i] + vals[hi]) / 3;
                var hgt = (float)Math.Min(v / scale, 1) * (ch - 2);
                pts[i] = new PointF(cx + i, cy + ch - 1 - hgt);
            }
            return pts;
        }

        // download: filled area + bright top edge
        var dl = Points(_histRx);
        using (var path = new GraphicsPath())
        {
            path.AddLine(cx, cy + ch - 1, dl[0].X, dl[0].Y);
            path.AddLines(dl);
            path.AddLine(dl[^1].X, dl[^1].Y, cx + cw - 1, cy + ch - 1);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(0, 84, 0));
            g.FillPath(fill, path);
        }
        using (var pen = new Pen(Green, 3) { LineJoin = LineJoin.Round })
        {
            g.DrawLines(pen, dl);
        }

        // upload: yellow line
        var ul = Points(_histTx);
        using (var pen = new Pen(Yellow, 3) { LineJoin = LineJoin.Round })
        {
            g.DrawLines(pen, ul);
        }

        // axis + footer labels
        using (var right = new StringFormat { Alignment = StringAlignment.Far })
        {
            g.DrawString(DeviceSpeedText(scale), labelFont, greyBrush,
                         new RectangleF(120, 46, 112, 12), right);
        }
        using (var center = new StringFormat { Alignment = StringAlignment.Center })
        {
            using var sysLabelFont = new Font("Consolas", 7f);
            using var sysValueFont = new Font("Consolas", 11.5f, FontStyle.Bold);
            g.DrawString("CPU", sysLabelFont, greyBrush, 28, 196);
            g.DrawString($"{NetCPU}%", sysValueFont, Brushes.White, 62, 189);
            g.DrawString("MEM", sysLabelFont, greyBrush, 130, 196);
            g.DrawString($"{NetMem}%", sysValueFont, Brushes.White, 164, 189);
            g.DrawString("SYSTEM MONITOR", labelFont, greyBrush,
                         new RectangleF(0, 212, 240, 12), center);
        }
    }

    /// Same compact unit strings the firmware prints ("2.3M", "480K").
    public static string DeviceSpeedText(double bps)
    {
        if (bps >= 1_000_000) return $"{bps / 1_000_000:F1}M";
        if (bps >= 1_000) return $"{bps / 1_000:F0}K";
        return $"{bps:F0}B";
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// MARK: - popup form (the popover)

sealed class MirrorForm : Form
{
    readonly StatusService _service;
    readonly NetSpeedMonitor _netMonitor;
    readonly NowPlayingMonitor _nowPlaying;
    readonly StockMonitor _stocks;
    readonly WeatherMonitor _weather;
    readonly MirrorControl _mirror = new();
    readonly RadioButton[] _modeButtons;
    static readonly string[] Modes = { "auto", "claude", "codex", "dual", "domestic", "net", "weather", "stock" };
    static readonly string[] ModeLabels = { "自动", "Claude", "Codex", "双额度", "国产", "监控", "天气", "股票" };
    readonly Label _statusLabel = new();
    readonly TrackBar _brightness = new() { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None };
    readonly Label _brightnessValue = new();
    // Drag streams many scroll events; posts to the single-threaded ESP8266 web
    // server are throttled mid-drag and the final value always flushes on mouse-up.
    int? _pendingBrightness;
    DateTime _lastBrightnessSentAt = DateTime.MinValue;

    readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 120 };
    readonly System.Windows.Forms.Timer _sweepTimer = new()
    {
        Interval = (int)(NetSpeedMonitor.SampleInterval * 1000),
    };

    readonly Dictionary<string, (int Rev, List<Bitmap> Frames, int W, int H)> _spriteCache = new();
    DeviceInfo _lastInfo;
    string _fetchingSlot;
    bool _applyingMode; // suppress CheckedChanged while reflecting device state

    public MirrorForm(StatusService service, NetSpeedMonitor netMonitor, NowPlayingMonitor nowPlaying,
                      StockMonitor stocks, WeatherMonitor weather)
    {
        _service = service;
        _netMonitor = netMonitor;
        _nowPlaying = nowPlaying;
        _stocks = stocks;
        _weather = weather;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = SystemColors.Control;
        Padding = new Padding(1);

        ClientSize = new Size(Px(360), Px(444));

        _mirror.SetBounds(Px(36), Px(14), Px(288), Px(288));
        Controls.Add(_mirror);

        _modeButtons = new RadioButton[Modes.Length];
        var segWidth = Px(332) / Modes.Length;
        for (int i = 0; i < Modes.Length; i++)
        {
            var btn = new RadioButton
            {
                Appearance = Appearance.Button,
                Text = ModeLabels[i],
                TextAlign = ContentAlignment.MiddleCenter,
                Tag = Modes[i],
                AutoSize = false,
                Font = new Font("Microsoft YaHei UI", 7.5f),
                FlatStyle = FlatStyle.Flat,
                Padding = Padding.Empty,
            };
            btn.SetBounds(Px(14) + i * segWidth, Px(312), segWidth, Px(28));
            btn.FlatAppearance.BorderColor = SystemColors.ControlDark;
            btn.CheckedChanged += ModeChanged;
            _modeButtons[i] = btn;
            Controls.Add(btn);
        }

        var sunLabel = new Label
        {
            Text = "☀",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
        };
        sunLabel.SetBounds(Px(12), Px(346), Px(24), Px(26));
        Controls.Add(sunLabel);
        _brightness.SetBounds(Px(36), Px(346), Px(260), Px(26));
        _brightness.Scroll += (_, _) => OnBrightnessInput(final: false);
        _brightness.MouseUp += (_, _) => OnBrightnessInput(final: true);
        Controls.Add(_brightness);
        _brightnessValue.SetBounds(Px(298), Px(346), Px(48), Px(26));
        _brightnessValue.TextAlign = ContentAlignment.MiddleRight;
        _brightnessValue.ForeColor = SystemColors.GrayText;
        _brightnessValue.Font = new Font("Microsoft YaHei UI", 8.5f);
        _brightnessValue.Text = "100%";
        Controls.Add(_brightnessValue);

        _statusLabel.SetBounds(Px(12), Px(376), Px(336), Px(58));
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Font = new Font("Microsoft YaHei UI", 8.5f);
        _statusLabel.Text = "连接设备中…";
        _statusLabel.AutoEllipsis = false;
        Controls.Add(_statusLabel);

        _pollTimer.Tick += async (_, _) => await Tick();
        _animTimer.Tick += (_, _) => AnimTick();
        _sweepTimer.Tick += (_, _) => SweepTick();
        Deactivate += (_, _) => HidePopup(); // transient, like NSPopover
    }

    float ScaleF() => DeviceDpi / 96f;
    int Px(int logical) => (int)Math.Round(logical * ScaleF());

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(120, 120, 120));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public void Toggle()
    {
        if (Visible)
        {
            HidePopup();
            return;
        }
        // anchor to the tray corner of the primary screen, near the cursor
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Min(Math.Max(Cursor.Position.X - Width / 2, area.Left + 8),
                         area.Right - Width - 8);
        var y = area.Bottom - Height - 8;
        Location = new Point(x, y);
        Show();
        Activate();
        _pollTimer.Start();
        _animTimer.Start();
        _sweepTimer.Start();
        _ = Tick();
    }

    void HidePopup()
    {
        Hide();
        _pollTimer.Stop();
        _animTimer.Stop();
        _sweepTimer.Stop();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HidePopup();
        }
        base.OnFormClosing(e);
    }

    void OnBrightnessInput(bool final)
    {
        var level = _brightness.Value;
        _brightnessValue.Text = $"{level}%";
        _pendingBrightness = level;
        if (!final && (DateTime.Now - _lastBrightnessSentAt).TotalMilliseconds < 250) return;
        FlushBrightness();
    }

    void FlushBrightness()
    {
        if (_pendingBrightness is not int level) return;
        _pendingBrightness = null;
        _lastBrightnessSentAt = DateTime.Now;
        _ = DeviceClient.SetBrightness(level);
    }

    /// Follow the device's reported brightness (changed via its web page or
    /// another client) — but never while the user is mid-adjustment here.
    void SyncBrightness(DeviceInfo info)
    {
        if (_pendingBrightness != null ||
            (DateTime.Now - _lastBrightnessSentAt).TotalSeconds < 2) return;
        var level = Math.Clamp(info.Brightness, 0, 100);
        _brightness.Value = level;
        _brightnessValue.Text = $"{level}%";
    }

    /// One sweep step: push the newest 4Hz sample, refresh the DL/UL readout.
    void SweepTick()
    {
        if (!_mirror.NetMode || !Visible) return;
        var cur = _netMonitor.Current;
        var smoothed = _netMonitor.CurrentSmoothed;
        _mirror.NetHeaderDL = MirrorControl.DeviceSpeedText(smoothed.Rx);
        _mirror.NetHeaderUL = MirrorControl.DeviceSpeedText(smoothed.Tx);
        var stats = SystemStatsMonitor.Snapshot();
        _mirror.NetCPU = stats.Cpu;
        _mirror.NetMem = stats.Mem;
        _mirror.PushNetSample(cur.Rx, cur.Tx);
    }

    async Task Tick()
    {
        DeviceInfo info;
        try
        {
            info = await DeviceClient.FetchInfo();
        }
        catch (Exception)
        {
            if (!Visible) return;
            _mirror.DeviceOK = false;
            _mirror.Invalidate();
            _statusLabel.Text = DeviceClient.Host.Length == 0
                ? "未设置设备地址（右键托盘 → 设置设备地址）" : $"无法连接 {DeviceClient.Host}";
            return;
        }
        if (!Visible) return;
        _lastInfo = info;
        _mirror.DeviceOK = true;
        ApplyScene(info);
        EnsureSprite(info);
        SyncBrightness(info);
        var modeIdx = Array.IndexOf(Modes, info.Mode);
        _applyingMode = true;
        foreach (var button in _modeButtons) button.Checked = false;
        if (modeIdx >= 0) _modeButtons[modeIdx].Checked = true;
        _applyingMode = false;
        var modeText = info.Mode == "auto" ? "自动切换"
            : info.Mode == "net" ? "系统监控"
            : info.Mode == "music" ? "音乐播放"
            : info.Mode == "screensaver" ? "屏保"
            : info.Mode == "dual" ? "Claude + Codex 额度" : "固定显示";
        _statusLabel.Text = $"{info.Ip} · {modeText}\n数据来源：{info.Bridge}";
    }

    /// Quota lines & ring exactly as the firmware computes them from /status.
    void ApplyScene(DeviceInfo info)
    {
        // mirror what's actually on the device screen (effective), so an
        // AUTO device that auto-switched to music shows music here too
        var enteringNet = info.Effective == "net" && !_mirror.NetMode;
        _mirror.ScreenSaverMode = info.Effective == "screensaver";
        _mirror.NetMode = info.Effective == "net";
        _mirror.MusicMode = info.Effective == "music";
        _mirror.DomesticMode = info.Effective == "domestic";
        _mirror.DualMode = info.Effective == "dual";
        _mirror.StockMode = info.Effective == "stock";
        _mirror.WeatherMode = info.Effective == "weather";
        if (_mirror.ScreenSaverMode)
        {
            _mirror.Invalidate();
            return;
        }
        if (_mirror.NetMode)
        {
            if (enteringNet) _mirror.ResetNetSweep(); // fresh sweep, like the device
            _mirror.Invalidate();
            return;
        }
        if (_mirror.MusicMode)
        {
            var s = _nowPlaying.Snapshot;
            _mirror.MusicTitle = s.Title;
            _mirror.MusicArtist = s.Artist;
            _mirror.MusicElapsed = s.Elapsed;
            _mirror.MusicDuration = s.Duration;
            _mirror.MusicPlaying = s.Playing;
            _mirror.MusicCover?.Dispose();
            var cover = _nowPlaying.CoverRgb565;
            _mirror.MusicCover = cover.Length > 0 ? Rgb565.Decode(cover, 0, 128, 128) : null;
            _mirror.Invalidate();
            return;
        }
        if (_mirror.StockMode)
        {
            _mirror.Stocks = _stocks.Snapshot;
            _mirror.Invalidate();
            return;
        }
        if (_mirror.WeatherMode)
        {
            _mirror.Weather = _weather.Current;
            _mirror.Invalidate();
            return;
        }
        var snap = _service.Snapshot();
        if (_mirror.DualMode)
        {
            _mirror.Dual = snap;
            _mirror.Invalidate();
            return;
        }
        if (_mirror.DomesticMode)
        {
            _mirror.Domestic = snap.Domestic;
            _mirror.NeedsInput = snap.Domestic.NeedsInput;
            _mirror.Invalidate();
            return;
        }
        _mirror.ShowingClaude = info.Showing != "codex";
        if (_mirror.ShowingClaude)
        {
            var pct = snap.Claude.FiveHourPct ?? 0;
            _mirror.RingPct = pct;
            _mirror.FiveHourPct = snap.Claude.FiveHourPct;
            _mirror.FiveHourResetMin = snap.Claude.FiveHourResetMin;
            _mirror.WeeklyPct = snap.Claude.SevenDayPct;
            _mirror.WeeklyResetMin = snap.Claude.SevenDayResetMin;
            _mirror.ResetCreditsAvailable = null;
            _mirror.ResetCreditExpiresAt = null;
            _mirror.Plan = snap.Claude.Plan;
            _mirror.NeedsInput = snap.Claude.NeedsInput;
        }
        else
        {
            _mirror.RingPct = snap.Codex.WeeklyPct ?? snap.Codex.PrimaryPct ?? 0;
            _mirror.FiveHourPct = snap.Codex.PrimaryPct;
            _mirror.FiveHourResetMin = snap.Codex.PrimaryResetMin;
            _mirror.WeeklyPct = snap.Codex.WeeklyPct;
            _mirror.WeeklyResetMin = snap.Codex.WeeklyResetMin;
            _mirror.ResetCreditsAvailable = snap.Codex.ResetCreditsAvailable;
            _mirror.ResetCreditExpiresAt = snap.Codex.ResetCreditExpiresAt;
            _mirror.Plan = snap.Codex.Plan;
            _mirror.NeedsInput = snap.Codex.NeedsInput;
        }
        _mirror.Invalidate();
    }

    static string PctText(double? pct) =>
        pct.HasValue && pct.Value >= 0 ? $"{(int)pct.Value}%" : "-";

    void EnsureSprite(DeviceInfo info)
    {
        if (info.Effective is "net" or "music" or "dual" or "domestic" or "stock" or "weather" or "screensaver") return;
        var slot = info.Showing == "codex" ? "codex" : "claude";
        var w = slot == "claude" ? info.ClaudeW : info.CodexW;
        var h = slot == "claude" ? info.ClaudeH : info.CodexH;
        if (_spriteCache.TryGetValue(slot, out var cached) && cached.Rev == info.SpriteRev)
        {
            _mirror.Frames = cached.Frames;
            _mirror.SpriteW = cached.W;
            _mirror.SpriteH = cached.H;
            return;
        }
        if (_fetchingSlot == slot) return;
        _fetchingSlot = slot;
        _ = FetchSprite(slot, info.SpriteRev, w, h);
    }

    async Task FetchSprite(string slot, int rev, int w, int h)
    {
        try
        {
            var data = await DeviceClient.FetchSpriteRaw(slot);
            var frames = Rgb565.DecodeSpriteFrames(data, w, h);
            if (frames.Count == 0) return;
            if (_spriteCache.TryGetValue(slot, out var old))
                foreach (var f in old.Frames) f.Dispose();
            _spriteCache[slot] = (rev, frames, w, h);
            if ((_lastInfo?.Showing == "codex" ? "codex" : "claude") == slot)
            {
                _mirror.Frames = frames;
                _mirror.SpriteW = w;
                _mirror.SpriteH = h;
                _mirror.Invalidate();
            }
        }
        catch (Exception)
        {
            // device unreachable / mid-upload: next tick retries
        }
        finally
        {
            _fetchingSlot = null;
        }
    }

    int _flashCounter;

    void AnimTick()
    {
        if (_lastInfo == null || _mirror.ScreenSaverMode || _mirror.NetMode || _mirror.MusicMode || _mirror.DualMode || _mirror.StockMode || _mirror.WeatherMode) return;

        // ~400ms red-border flash while an approval is pending (device cadence)
        if (_mirror.NeedsInput)
        {
            _flashCounter++;
            if (_flashCounter >= 3) // 3 * 0.12s ≈ 0.36s
            {
                _flashCounter = 0;
                _mirror.FlashOn = !_mirror.FlashOn;
                _mirror.Invalidate();
            }
        }
        else if (_mirror.FlashOn)
        {
            _mirror.FlashOn = false;
            _mirror.Invalidate();
        }

        if (_mirror.DomesticMode || _mirror.Frames.Count == 0) return;
        var snap = _service.Snapshot();
        var working = _lastInfo.Showing == "codex"
            ? snap.Codex.Status == "working" : snap.Claude.Status == "working";
        if (working)
        {
            _mirror.FrameIdx = (_mirror.FrameIdx + 1) % _mirror.Frames.Count;
        }
        else if (_mirror.FrameIdx != 0)
        {
            _mirror.FrameIdx = 0;
        }
        _mirror.Invalidate();
    }

    async void ModeChanged(object sender, EventArgs e)
    {
        if (_applyingMode || sender is not RadioButton { Checked: true, Tag: string mode }) return;
        try
        {
            await DeviceClient.SetDisplayMode(mode);
        }
        catch (Exception)
        {
            // Tick() below re-syncs the buttons to the device's real state
        }
        await Tick();
    }
}
