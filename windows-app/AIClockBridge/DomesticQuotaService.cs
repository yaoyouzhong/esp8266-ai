using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIClockBridge;

sealed record DomesticProviderDefinition(
    string Id, string Name, string Product, string Url, bool CaptureSupported);

static class DomesticProviderCatalog
{
    public static readonly DomesticProviderDefinition[] All =
    {
        new("qwen", "阿里云百炼", "千问 Token Plan",
            "https://bailian.console.aliyun.com/cn-beijing?tab=plan#/efm/subscription/token-plan", true),
        new("kimi", "月之暗面", "Kimi Coding Plan", "https://www.kimi.com/code/console", true),
        new("xiaomi", "小米 MiMo", "MiMo Token Plan",
            "https://platform.xiaomimimo.com/token-plan", false),
        new("zhipu", "智谱 AI", "GLM / Coding Plan", "https://open.bigmodel.cn/usercenter", false),
        new("volcengine", "火山方舟", "豆包大模型", "https://console.volcengine.com/ark", false),
        new("minimax", "MiniMax", "MiniMax Token Plan", "https://platform.minimaxi.com/console/usage", true),
        new("deepseek", "DeepSeek", "DeepSeek 开放平台", "https://platform.deepseek.com/usage", false),
        new("baidu", "百度智能云", "千帆 / 文心", "https://console.bce.baidu.com/qianfan/overview", false),
        new("tencent", "腾讯云", "混元大模型", "https://console.cloud.tencent.com/hunyuan", false),
        new("huawei", "华为云", "盘古 / ModelArts", "https://console.huaweicloud.com/modelarts/", false),
        new("iflytek", "讯飞开放平台", "讯飞星火", "https://console.xfyun.cn/", false),
        new("stepfun", "阶跃星辰", "StepFun", "https://platform.stepfun.com/", false),
        new("baichuan", "百川智能", "Baichuan", "https://platform.baichuan-ai.com/", false),
        new("lingyi", "零一万物", "Yi", "https://platform.lingyiwanwu.com/", false),
    };
}

sealed class DomesticQuotaSnapshot
{
    public double? QwenPlanPct;
    public double? QwenWeeklyPct;
    public double? QwenFiveHourPct;
    public double? XiaomiPlanPct;
    public double? KimiWeeklyPct;
    public double? KimiFiveHourPct;
    public double? MiniMaxWeeklyPct;
    public double? MiniMaxFiveHourPct;
    public DateTimeOffset? QwenPlanResetAt;
    public DateTimeOffset? QwenWeeklyResetAt;
    public DateTimeOffset? QwenFiveHourResetAt;
    public DateTimeOffset? KimiWeeklyResetAt;
    public DateTimeOffset? KimiFiveHourResetAt;
    public DateTimeOffset? MiniMaxWeeklyResetAt;
    public DateTimeOffset? MiniMaxFiveHourResetAt;
    public string QwenMembership = "";
    public string KimiMembership = "";
    public string MiniMaxMembership = "";
    public DateTime? QwenFetchedAt;
    public DateTime? XiaomiFetchedAt;
    public DateTime? KimiFetchedAt;
    public DateTime? MiniMaxFetchedAt;
}

/// Reads the same read-only quota responses as the vendors' own account pages.
/// Passwords never pass through the app: sign-in happens inside the vendor page
/// and WebView2 owns its isolated persistent browser profile.
sealed class DomesticQuotaService
{
    internal const string QwenSubscriptionName = "Token Plan 团队版";
    const string MiniMaxCredentialTarget = "AIClockBridge/MiniMaxTokenPlanKey";
    const string MiniMaxQuotaEndpoint = "https://www.minimaxi.com/v1/token_plan/remains";
    static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(300);
    static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIClockBridge", "domestic-quota-cache.json");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly object _lock = new();
    readonly Dictionary<string, DateTime> _lastRefreshAttempt = new();
    readonly Dictionary<string, DateTime> _backoffUntil = new();
    DomesticQuotaSnapshot _snapshot = Load();
    DomesticQuotaAuthForm _form;

    public DomesticQuotaSnapshot Snapshot
    {
        get
        {
            lock (_lock) return new DomesticQuotaSnapshot
            {
                QwenPlanPct = _snapshot.QwenPlanPct,
                QwenWeeklyPct = _snapshot.QwenWeeklyPct,
                QwenFiveHourPct = _snapshot.QwenFiveHourPct,
                XiaomiPlanPct = _snapshot.XiaomiPlanPct,
                KimiWeeklyPct = _snapshot.KimiWeeklyPct,
                KimiFiveHourPct = _snapshot.KimiFiveHourPct,
                MiniMaxWeeklyPct = _snapshot.MiniMaxWeeklyPct,
                MiniMaxFiveHourPct = _snapshot.MiniMaxFiveHourPct,
                QwenPlanResetAt = _snapshot.QwenPlanResetAt,
                QwenWeeklyResetAt = _snapshot.QwenWeeklyResetAt,
                QwenFiveHourResetAt = _snapshot.QwenFiveHourResetAt,
                KimiWeeklyResetAt = _snapshot.KimiWeeklyResetAt,
                KimiFiveHourResetAt = _snapshot.KimiFiveHourResetAt,
                MiniMaxWeeklyResetAt = _snapshot.MiniMaxWeeklyResetAt,
                MiniMaxFiveHourResetAt = _snapshot.MiniMaxFiveHourResetAt,
                QwenMembership = _snapshot.QwenMembership,
                KimiMembership = _snapshot.KimiMembership,
                MiniMaxMembership = _snapshot.MiniMaxMembership,
                QwenFetchedAt = _snapshot.QwenFetchedAt,
                XiaomiFetchedAt = _snapshot.XiaomiFetchedAt,
                KimiFetchedAt = _snapshot.KimiFetchedAt,
                MiniMaxFetchedAt = _snapshot.MiniMaxFetchedAt,
            };
        }
    }

    public void OpenAuthorization(string providerId = "qwen")
    {
        if (_form == null || _form.IsDisposed)
            _form = new DomesticQuotaAuthForm(this, initialProviderId: providerId);
        _form.ShowAuthorization(providerId);
    }

    public void Refresh(string providerId, bool force = false)
    {
        var provider = DomesticProviderCatalog.All.FirstOrDefault(x => x.Id == providerId);
        if (provider?.CaptureSupported != true) return;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_backoffUntil.TryGetValue(providerId, out var blockedUntil)
                && now < blockedUntil) return;
            if (!force && _lastRefreshAttempt.TryGetValue(providerId, out var lastAttempt)
                && now - lastAttempt < MinRefreshInterval) return;
            _lastRefreshAttempt[providerId] = now;
        }
        if (providerId == "minimax" && HasMiniMaxApiKey)
        {
            _ = RefreshMiniMaxWithApiKey();
            return;
        }
        if (_form == null || _form.IsDisposed)
            _form = new DomesticQuotaAuthForm(this, initialProviderId: providerId);
        _form.RefreshInBackground(providerId);
    }

    internal bool HasMiniMaxApiKey => MiniMaxApiKey().Length > 0;

    internal void SaveMiniMaxApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        CredentialStore.Write(MiniMaxCredentialTarget, key.Trim());
    }

    internal async Task<(bool Success, string Message)> RefreshMiniMaxWithApiKey()
    {
        var key = MiniMaxApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return (false, "尚未保存 MiniMax Subscription Key / API Key。");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MiniMaxQuotaEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", key.Trim());
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request);
            if ((int)response.StatusCode == 429)
            {
                BackOff("minimax");
                return (false, "MiniMax 额度接口限流，5 分钟后自动重试。");
            }
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var usage = DomesticQuotaAuthForm.FindMiniMaxUsage(doc.RootElement);
            if (!usage.HasValue)
                return (false, response.IsSuccessStatusCode
                    ? "MiniMax 响应中没有可识别的额度字段。"
                    : $"MiniMax 额度接口返回 HTTP {(int)response.StatusCode}。");
            SetMiniMax(usage.Value.WeeklyPct, usage.Value.FiveHourPct,
                usage.Value.Membership, usage.Value.WeeklyResetAt, usage.Value.FiveHourResetAt);
            return (true, "已通过 MiniMax API 取得准确用量。");
        }
        catch (Exception ex)
        {
            return (false, $"MiniMax API 读取失败：{ex.Message}");
        }
    }

    static string MiniMaxApiKey()
    {
        var saved = CredentialStore.Read(MiniMaxCredentialTarget).Trim();
        if (saved.Length > 0) return saved;
        foreach (var name in new[]
                 {
                     "MINIMAX_SUBSCRIPTION_KEY", "MINIMAX_TOKEN_PLAN_KEY",
                     "MINIMAX_API_KEY"
                 })
        {
            var value = Environment.GetEnvironmentVariable(name)?.Trim() ?? "";
            if (value.Length > 0) return value;
        }
        return "";
    }

    internal void BackOff(string providerId)
    {
        lock (_lock)
        {
            var next = DateTime.UtcNow + RateLimitBackoff;
            if (!_backoffUntil.TryGetValue(providerId, out var current) || current < next)
                _backoffUntil[providerId] = next;
        }
    }

    internal void SetQwen(double pct, string membership = null, DateTimeOffset? resetAt = null)
    {
        lock (_lock)
        {
            _snapshot.QwenPlanPct = Clamp(pct);
            if (!string.IsNullOrWhiteSpace(membership))
                _snapshot.QwenMembership = membership.Trim();
            if (resetAt.HasValue) _snapshot.QwenPlanResetAt = resetAt;
            _snapshot.QwenFetchedAt = DateTime.UtcNow;
            Save();
        }
    }

    internal void SetQwenPageMetadata(string resetText, string membership)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(membership))
            {
                var incoming = membership.Trim();
                var genericTokenPlan = incoming.Equals("Token Plan", StringComparison.OrdinalIgnoreCase);
                if (!genericTokenPlan || string.IsNullOrWhiteSpace(_snapshot.QwenMembership))
                    _snapshot.QwenMembership = incoming;
            }
            if (ParseResetAt(resetText) is DateTimeOffset resetAt)
                _snapshot.QwenPlanResetAt = resetAt;
            Save();
        }
    }

    internal void SetXiaomi(double pct)
    {
        lock (_lock) { _snapshot.XiaomiPlanPct = Clamp(pct); _snapshot.XiaomiFetchedAt = DateTime.UtcNow; Save(); }
    }

    internal void SetKimi(double weeklyPct, double? fiveHourPct, string membership = null,
        DateTimeOffset? weeklyResetAt = null, DateTimeOffset? fiveHourResetAt = null)
    {
        lock (_lock)
        {
            _snapshot.KimiWeeklyPct = Clamp(weeklyPct);
            _snapshot.KimiFiveHourPct = fiveHourPct.HasValue ? Clamp(fiveHourPct.Value) : null;
            if (weeklyResetAt.HasValue) _snapshot.KimiWeeklyResetAt = weeklyResetAt;
            if (fiveHourResetAt.HasValue) _snapshot.KimiFiveHourResetAt = fiveHourResetAt;
            if (!string.IsNullOrWhiteSpace(membership))
            {
                _snapshot.KimiMembership = membership.Trim();
                Settings.Set("kimi_membership", _snapshot.KimiMembership);
            }
            _snapshot.KimiFetchedAt = DateTime.UtcNow;
            Save();
        }
    }

    internal void SetMiniMax(double weeklyPct, double? fiveHourPct, string membership = null,
        DateTimeOffset? weeklyResetAt = null, DateTimeOffset? fiveHourResetAt = null)
    {
        lock (_lock)
        {
            _snapshot.MiniMaxWeeklyPct = Clamp(weeklyPct);
            _snapshot.MiniMaxFiveHourPct = fiveHourPct.HasValue ? Clamp(fiveHourPct.Value) : null;
            if (weeklyResetAt.HasValue) _snapshot.MiniMaxWeeklyResetAt = weeklyResetAt;
            if (fiveHourResetAt.HasValue) _snapshot.MiniMaxFiveHourResetAt = fiveHourResetAt;
            if (!string.IsNullOrWhiteSpace(membership))
                _snapshot.MiniMaxMembership = membership.Trim();
            _snapshot.MiniMaxFetchedAt = DateTime.UtcNow;
            Save();
        }
    }

    internal void SetKimiMembership(string membership)
    {
        if (string.IsNullOrWhiteSpace(membership)) return;
        lock (_lock)
        {
            _snapshot.KimiMembership = membership.Trim();
            Settings.Set("kimi_membership", _snapshot.KimiMembership);
            Save();
        }
    }

    internal void SetKimiPageMetadata(string membership, string weeklyResetText,
        string fiveHourResetText)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(membership)
                && !membership.StartsWith("LEVEL_", StringComparison.OrdinalIgnoreCase))
            {
                _snapshot.KimiMembership = membership.Trim();
                Settings.Set("kimi_membership", _snapshot.KimiMembership);
            }
            if (ParseResetAt(weeklyResetText) is DateTimeOffset weeklyResetAt
                && (!_snapshot.KimiWeeklyResetAt.HasValue
                    || _snapshot.KimiWeeklyResetAt <= DateTimeOffset.Now))
                _snapshot.KimiWeeklyResetAt = weeklyResetAt;
            if (ParseResetAt(fiveHourResetText) is DateTimeOffset fiveHourResetAt
                && (!_snapshot.KimiFiveHourResetAt.HasValue
                    || _snapshot.KimiFiveHourResetAt <= DateTimeOffset.Now))
                _snapshot.KimiFiveHourResetAt = fiveHourResetAt;
            Save();
        }
    }

    internal static DateTimeOffset? ParseResetAt(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsedOffset))
            return parsedOffset;
        foreach (var format in new[]
                 {
                     "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm:ss",
                     "yyyy.MM.dd HH:mm:ss", "yyyy.MM.dd HH:mm"
                 })
        {
            if (!DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var local)) continue;
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        double minutes = 0;
        var matched = false;
        foreach (Match match in Regex.Matches(text,
                     @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>天|小时|时|分钟|分|days?|d|hours?|hrs?|h|minutes?|mins?|m)",
                     RegexOptions.IgnoreCase))
        {
            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value)) continue;
            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            minutes += unit.StartsWith("天") || unit.StartsWith("day") || unit == "d" ? value * 1440
                : unit.StartsWith("小时") || unit == "时" || unit.StartsWith("hour") || unit.StartsWith("hr")
                    || unit == "h"
                    ? value * 60 : value;
            matched = true;
        }
        return matched && minutes > 0 ? DateTimeOffset.Now.AddMinutes(minutes) : null;
    }

    static double Clamp(double value) => Math.Clamp(value, 0, 100);

    static DomesticQuotaSnapshot Load()
    {
        DomesticQuotaSnapshot snapshot;
        try { snapshot = JsonSerializer.Deserialize<DomesticQuotaSnapshot>(File.ReadAllText(CachePath),
            new JsonSerializerOptions { IncludeFields = true }) ?? new(); }
        catch { snapshot = new(); }
        if (snapshot.KimiMembership.StartsWith("LEVEL_", StringComparison.OrdinalIgnoreCase))
            snapshot.KimiMembership = "";
        if (string.IsNullOrWhiteSpace(snapshot.KimiMembership))
        {
            var savedMembership = Settings.Get("kimi_membership");
            if (!savedMembership.StartsWith("LEVEL_", StringComparison.OrdinalIgnoreCase))
                snapshot.KimiMembership = savedMembership;
        }
        return snapshot;
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(_snapshot,
                new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
        catch { }
    }
}

sealed class DomesticQuotaAuthForm : Form
{
    const int GwlExStyle = -20;
    const long WsExTransparent = 0x00000020L;
    const long WsExNoActivate = 0x08000000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern nint SetWindowLongPtr(nint window, int index, nint value);

    static readonly TimeSpan LoginRetention = TimeSpan.FromDays(30);

    readonly DomesticQuotaService _service;
    readonly bool _hideOnUserClose;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly Dictionary<string, (string ProviderId, string Endpoint)> _quotaResponses = new();
    readonly Dictionary<string, Panel> _providerCards = new();
    CoreWebView2DevToolsProtocolEventReceiver _responseReceiver;
    CoreWebView2DevToolsProtocolEventReceiver _finishedReceiver;
    DomesticProviderDefinition _activeProvider;
    string _initialProviderId;
    int _navigationGeneration;
    bool _capturedForNavigation;
    bool _everShown;
    bool _backgroundRefresh;
    readonly Label _providerTitle = new()
    {
        AutoSize = true, Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold),
        ForeColor = Color.FromArgb(31, 41, 55), Location = new Point(20, 14),
    };
    readonly Label _providerState = new()
    {
        AutoSize = true, Font = new Font("Microsoft YaHei UI", 9), Location = new Point(21, 45),
    };
    readonly Label _status = new()
    {
        Dock = DockStyle.Bottom, Height = 40, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(14, 0, 0, 0), BackColor = Color.FromArgb(248, 250, 252),
        ForeColor = Color.FromArgb(71, 85, 105),
        Text = "选择左侧厂商。已支持的厂商在登录后会自动读取准确额度。",
    };
    readonly Panel _miniMaxKeyPanel = new()
    {
        Dock = DockStyle.Top, Height = 52, Padding = new Padding(20, 8, 20, 8),
        BackColor = Color.FromArgb(248, 250, 252), Visible = false,
    };
    readonly TextBox _miniMaxApiKey = new()
    {
        UseSystemPasswordChar = true,
        PlaceholderText = "MiniMax Subscription Key / API Key，留空则只测试已保存 Key",
    };
    readonly Button _miniMaxSaveKey = new()
    {
        Text = "保存并测试", Width = 108, Height = 32,
        FlatStyle = FlatStyle.Flat, BackColor = Color.White,
        ForeColor = Color.FromArgb(51, 65, 85),
    };

    public DomesticQuotaAuthForm(DomesticQuotaService service, bool hideOnUserClose = true,
                                 string initialProviderId = "qwen")
    {
        _service = service;
        _hideOnUserClose = hideOnUserClose;
        _initialProviderId = initialProviderId;
        Text = "国产模型额度授权";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        AutoScaleMode = AutoScaleMode.Dpi;
        Width = 1180;
        Height = 800;
        MinimumSize = new Size(900, 620);
        BackColor = Color.White;

        var navigation = new Panel
        {
            Dock = DockStyle.Left, Width = 270, Padding = new Padding(14, 16, 10, 12),
            BackColor = Color.FromArgb(245, 247, 250),
        };
        var navigationTitle = new Label
        {
            Dock = DockStyle.Top, Height = 54, Text = "国产模型厂商\r\n选择后在右侧完成登录",
            Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 41, 59),
        };
        var providerList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(0, 4, 0, 8),
        };
        navigation.Controls.Add(providerList);
        navigation.Controls.Add(navigationTitle);

        foreach (var provider in DomesticProviderCatalog.All) providerList.Controls.Add(BuildProviderCard(provider));

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var header = new Panel
        {
            Dock = DockStyle.Top, Height = 76, BackColor = Color.White,
            Padding = new Padding(0),
        };
        var refresh = new Button
        {
            Text = "刷新页面", Width = 112, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White,
            ForeColor = Color.FromArgb(51, 65, 85), Location = new Point(760, 22),
        };
        refresh.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        refresh.Click += (_, _) => _web.CoreWebView2?.Reload();
        header.Resize += (_, _) => refresh.Left = header.ClientSize.Width - refresh.Width - 18;
        header.Controls.Add(_providerTitle);
        header.Controls.Add(_providerState);
        header.Controls.Add(refresh);
        _miniMaxSaveKey.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        _miniMaxSaveKey.Click += async (_, _) => await SaveAndTestMiniMaxKey();
        _miniMaxKeyPanel.Controls.Add(_miniMaxApiKey);
        _miniMaxKeyPanel.Controls.Add(_miniMaxSaveKey);
        _miniMaxKeyPanel.Resize += (_, _) => LayoutMiniMaxKeyPanel();
        LayoutMiniMaxKeyPanel();
        content.Controls.Add(_web);
        content.Controls.Add(_status);
        content.Controls.Add(_miniMaxKeyPanel);
        content.Controls.Add(header);
        Controls.Add(content);
        Controls.Add(navigation);

        Shown += async (_, _) =>
        {
            _everShown = true;
            await SelectProvider(ProviderById(_initialProviderId));
        };
    }

    public void ShowAuthorization(string providerId)
    {
        _initialProviderId = providerId;
        _backgroundRefresh = false;
        Enabled = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(900, 620);
        if (_providerTitle.Parent != null) _providerTitle.Parent.Visible = true;
        _status.Visible = true;
        ShowInTaskbar = true;
        Opacity = 1;
        var targetScreen = Screen.FromPoint(Cursor.Position);
        StartPosition = FormStartPosition.Manual;
        WindowState = FormWindowState.Normal;
        Bounds = targetScreen.WorkingArea;
        TopMost = true;
        var wasEverShown = _everShown;
        if (!Visible) Show();
        SetBackgroundWindowStyle(false);
        WindowState = FormWindowState.Maximized;
        if (wasEverShown) BeginInvoke(async () => await SelectProvider(ProviderById(providerId)));
        BringToFront();
        Activate();
        BeginInvoke(() => TopMost = false);
    }

    public void RefreshInBackground(string providerId)
    {
        if (Visible && !_backgroundRefresh) return; // never interrupt an interactive login
        _initialProviderId = providerId;
        _backgroundRefresh = true;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        MinimumSize = Size.Empty;
        if (_providerTitle.Parent != null) _providerTitle.Parent.Visible = false;
        _status.Visible = false;
        // Alibaba only creates its signed quota POST while WebView2 owns a
        // renderable viewport. A nearly invisible 16px viewport is enough;
        // click-through + no-activate styles keep it out of the user's way.
        Opacity = 0.01;
        StartPosition = FormStartPosition.Manual;
        WindowState = FormWindowState.Normal;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Bounds = new Rectangle(area.Right - 16, area.Top, 16, 16);
        TopMost = true;
        var wasEverShown = _everShown;
        if (!Visible) Show();
        SetBackgroundWindowStyle(true);
        if (wasEverShown) BeginInvoke(async () => await SelectProvider(ProviderById(providerId)));
    }

    static DomesticProviderDefinition ProviderById(string providerId) =>
        DomesticProviderCatalog.All.FirstOrDefault(x => x.Id == providerId)
        ?? DomesticProviderCatalog.All[0];

    protected override bool ShowWithoutActivation => _backgroundRefresh;

    void SetBackgroundWindowStyle(bool background)
    {
        if (!IsHandleCreated) return;
        var style = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        var flags = WsExTransparent | WsExNoActivate;
        style = background ? style | flags : style & ~flags;
        SetWindowLongPtr(Handle, GwlExStyle, new nint(style));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_hideOnUserClose && e.CloseReason == CloseReason.UserClosing)
        {
            if (_activeProvider?.Id is "qwen" or "kimi" or "minimax") _ = PersistLoginCookies(_activeProvider.Url);
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    Panel BuildProviderCard(DomesticProviderDefinition provider)
    {
        var card = new Panel
        {
            Width = 210, Height = 62, Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.White, Cursor = Cursors.Hand, Tag = provider.Id,
        };
        var name = new Label
        {
            Text = provider.Name, AutoSize = false, Width = 132, Height = 24,
            Location = new Point(12, 8), Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 41, 59), Cursor = Cursors.Hand,
        };
        var product = new Label
        {
            Text = provider.Product, AutoSize = false, Width = 184, Height = 20,
            Location = new Point(12, 34), Font = new Font("Microsoft YaHei UI", 8),
            ForeColor = Color.FromArgb(100, 116, 139), Cursor = Cursors.Hand,
        };
        var badge = new Label
        {
            Text = provider.CaptureSupported ? "可用" : "待接", AutoSize = false,
            Width = 62, Height = 21, Location = new Point(140, 8), TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Bold), Cursor = Cursors.Hand,
            ForeColor = provider.CaptureSupported ? Color.FromArgb(22, 101, 52) : Color.FromArgb(100, 116, 139),
            BackColor = provider.CaptureSupported ? Color.FromArgb(220, 252, 231) : Color.FromArgb(241, 245, 249),
        };
        async void Click(object sender, EventArgs e) => await SelectProvider(provider);
        card.Click += Click;
        name.Click += Click;
        product.Click += Click;
        badge.Click += Click;
        card.Controls.Add(name);
        card.Controls.Add(product);
        card.Controls.Add(badge);
        _providerCards[provider.Id] = card;
        return card;
    }

    async Task SelectProvider(DomesticProviderDefinition provider)
    {
        _activeProvider = provider;
        foreach (var (id, card) in _providerCards)
            card.BackColor = id == provider.Id ? Color.FromArgb(224, 242, 254) : Color.White;
        _providerTitle.Text = $"{provider.Name} · {provider.Product}";
        _miniMaxKeyPanel.Visible = provider.Id == "minimax";
        _providerState.Text = provider.Id == "minimax"
            ? "已支持 API 查询：保存 MiniMax Subscription Key 后自动读取；也可登录控制台作为兜底"
            : provider.CaptureSupported
            ? "已支持准确额度读取：登录后进入订阅/用量页面即可自动捕获"
            : "已列入厂商目录：可以登录控制台，准确额度读取规则尚待适配";
        _providerState.ForeColor = provider.CaptureSupported
            ? Color.FromArgb(22, 101, 52) : Color.FromArgb(180, 83, 9);
        await Navigate(provider);
    }

    void LayoutMiniMaxKeyPanel()
    {
        var buttonWidth = _miniMaxSaveKey.Width;
        _miniMaxSaveKey.Location = new Point(_miniMaxKeyPanel.ClientSize.Width
            - buttonWidth - 20, 10);
        _miniMaxApiKey.Location = new Point(20, 10);
        _miniMaxApiKey.Width = Math.Max(120, _miniMaxSaveKey.Left - 30);
        _miniMaxApiKey.Height = 30;
    }

    async Task SaveAndTestMiniMaxKey()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_miniMaxApiKey.Text))
            {
                _service.SaveMiniMaxApiKey(_miniMaxApiKey.Text);
                _miniMaxApiKey.Clear();
            }
            if (!_service.HasMiniMaxApiKey)
            {
                _status.Text = "请先粘贴 MiniMax Subscription Key / API Key。";
                return;
            }
            _status.Text = "正在通过 MiniMax API 查询额度...";
            var result = await _service.RefreshMiniMaxWithApiKey();
            _capturedForNavigation = result.Success;
            _status.Text = result.Message;
        }
        catch (Exception ex)
        {
            _status.Text = $"MiniMax Key 保存或测试失败：{ex.Message}";
        }
    }

    async Task Navigate(DomesticProviderDefinition provider)
    {
        var generation = ++_navigationGeneration;
        _capturedForNavigation = false;
        if (_web.CoreWebView2 == null)
        {
            var profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AIClockBridge", "quota-auth-profile");
            var env = await CoreWebView2Environment.CreateAsync(null, profile);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            _responseReceiver = _web.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.responseReceived");
            _finishedReceiver = _web.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
            _responseReceiver.DevToolsProtocolEventReceived += CdpResponseReceived;
            _finishedReceiver.DevToolsProtocolEventReceived += CdpLoadingFinished;
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess && _activeProvider?.Id is "qwen" or "kimi" or "minimax")
                    await PersistLoginCookies(_activeProvider.Url);
                if (e.IsSuccess && _activeProvider?.Id == "kimi")
                    _ = CaptureKimiPageMetadata(_navigationGeneration);
                if (e.IsSuccess && _activeProvider?.Id == "qwen")
                {
                    _ = CaptureQwenPageMetadata(_navigationGeneration);
                }
            };
        }
        _status.Text = provider.Id == "minimax" && _service.HasMiniMaxApiKey
            ? "MiniMax Key 已保存；后台刷新优先通过官方 API 查询额度。"
            : provider.CaptureSupported
            ? $"请登录{provider.Name}；捕获到准确用量后会自动缓存百分比。"
            : $"{provider.Name}已提供统一登录入口；当前版本暂不读取其额度数字。";
        _web.CoreWebView2.Navigate(provider.Url);
        if (provider.CaptureSupported) _ = ShowPendingStatus(provider, generation);
    }

    async Task CaptureKimiPageMetadata(int generation)
    {
        const string script = """
            (() => {
              const cards = Array.from(document.querySelectorAll('.stats-card'));
              const card = cards.find(item => {
                const title = item.querySelector('.stats-card-title');
                const text = title ? title.textContent.trim() : '';
                return text === '我的权益' || text === 'My Benefits';
              });
              const value = card ? card.querySelector('.stats-card-value') : null;
              const metric = pattern => {
                const containers = Array.from(document.querySelectorAll(
                  '.stats-card, .combined-card-quota, .combined-card-item'));
                const container = containers.find(item => pattern.test(item.innerText || ''));
                const match = container?.innerText?.match(/(\d+(?:\.\d+)?)\s*%/);
                return match ? Number(match[1]) : null;
              };
              const entries = Array.from(document.querySelectorAll(
                  '.stats-card-reset-time, .combined-card-reset-time'))
                .map(item => ({
                  text: item.textContent.trim(),
                  context: (item.closest('.combined-card-quota, .stats-card') || item.parentElement)
                    ?.innerText || ''
                })).filter(item => item.text);
              const unique = entries.filter((item, index) =>
                entries.findIndex(other => other.text === item.text) === index);
              const weekly = unique.find(item => /本周用量|weekly\s+usage/i.test(item.context));
              const fiveHour = unique.find(item => /频限明细|rate\s+limit/i.test(item.context));
              return {
                membership: value ? value.textContent.trim() : '',
                weeklyPct: metric(/本周用量|weekly\s+usage/i),
                fiveHourPct: metric(/频限明细|rate\s+limit/i),
                weeklyResetText: weekly?.text || '',
                fiveHourResetText: fiveHour?.text || ''
              };
            })()
            """;
        double? previousWeeklyPct = null;
        double? previousFiveHourPct = null;
        var previousWeeklyReset = "";
        var previousFiveHourReset = "";
        var stableReadCount = 0;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (IsDisposed || generation != _navigationGeneration || _activeProvider?.Id != "kimi"
                || _web.CoreWebView2 == null) return;
            try
            {
                var result = await _web.CoreWebView2.ExecuteScriptAsync(script);
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                var membership = root.TryGetProperty("membership", out var membershipValue)
                    ? membershipValue.GetString()?.Trim() ?? "" : "";
                var weeklyReset = root.TryGetProperty("weeklyResetText", out var weeklyValue)
                    ? weeklyValue.GetString()?.Trim() ?? "" : "";
                var fiveHourReset = root.TryGetProperty("fiveHourResetText", out var fiveHourValue)
                    ? fiveHourValue.GetString()?.Trim() ?? "" : "";
                var weeklyPct = root.TryGetProperty("weeklyPct", out var weeklyPctValue)
                    && weeklyPctValue.ValueKind == JsonValueKind.Number
                    ? weeklyPctValue.GetDouble() : (double?)null;
                var fiveHourPct = root.TryGetProperty("fiveHourPct", out var fiveHourPctValue)
                    && fiveHourPctValue.ValueKind == JsonValueKind.Number
                    ? fiveHourPctValue.GetDouble() : (double?)null;
                if (weeklyPct.HasValue && fiveHourPct.HasValue
                    && weeklyReset.Length > 0 && fiveHourReset.Length > 0)
                {
                    stableReadCount = weeklyPct == previousWeeklyPct
                        && fiveHourPct == previousFiveHourPct
                        && weeklyReset == previousWeeklyReset
                        && fiveHourReset == previousFiveHourReset ? stableReadCount + 1 : 1;
                    previousWeeklyPct = weeklyPct;
                    previousFiveHourPct = fiveHourPct;
                    previousWeeklyReset = weeklyReset;
                    previousFiveHourReset = fiveHourReset;
                    if (stableReadCount >= 2)
                    {
                        _service.SetKimi(weeklyPct.Value, fiveHourPct, membership,
                            DomesticQuotaService.ParseResetAt(weeklyReset),
                            DomesticQuotaService.ParseResetAt(fiveHourReset));
                        _capturedForNavigation = true;
                        Console.Error.WriteLine(
                            $"[quota] Kimi DOM updated: Weekly {weeklyPct.Value:0.##}%, "
                            + $"5H {fiveHourPct.Value:0.##}%");
                        BeginInvoke(() => _status.Text =
                            $"已取得 Kimi 准确用量：Weekly {weeklyPct.Value:0.##}%，"
                            + $"5h {fiveHourPct.Value:0.##}%（已缓存）");
                        if (_backgroundRefresh) BeginInvoke(Hide);
                        return;
                    }
                }
            }
            catch
            {
                // The Vue page may still be replacing its initial DOM; retry below.
            }
            await Task.Delay(500);
        }
    }

    async Task CaptureQwenPageMetadata(int generation)
    {
        const string script = """
            (() => {
              const text = document.body ? document.body.innerText : '';
              const reset = text.match(/重置时间\s*([0-9]{4}[-\/.]\d{1,2}[-\/.]\d{1,2}\s+\d{1,2}:\d{2}(?::\d{2})?)/);
              const candidates = Array.from(document.querySelectorAll(
                '[class*="card"], [class*="panel"], [class*="quota"], [class*="usage"]'))
                .map(node => node.innerText || '')
                .filter(value => /用量消耗|usage\s+consumption/i.test(value) && /\d+(?:\.\d+)?\s*%/.test(value))
                .sort((a, b) => a.length - b.length);
              const marker = text.search(/用量消耗|usage\s+consumption/i);
              const fallback = marker >= 0 ? text.slice(marker, marker + 800) : '';
              const usage = (candidates[0] || fallback).match(/(\d+(?:\.\d+)?)\s*%/);
              let membership = '';
              if (/Coding\s*Plan/i.test(text)) membership = 'Coding Plan';
              else if (/Token\s*Plan/i.test(text) && /团队版/i.test(text)) membership = 'Token Plan 团队版';
              else if (/Token\s*Plan/i.test(text)) membership = 'Token Plan';
              return {
                resetText: reset ? reset[1] : '',
                usagePctText: usage ? usage[1] : '',
                membership
              };
            })()
            """;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (IsDisposed || generation != _navigationGeneration || _activeProvider?.Id != "qwen"
                || _web.CoreWebView2 == null || _capturedForNavigation) return;
            try
            {
                var result = await _web.CoreWebView2.ExecuteScriptAsync(script);
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                var reset = root.TryGetProperty("resetText", out var resetValue)
                    ? resetValue.GetString()?.Trim() ?? "" : "";
                var usagePctText = root.TryGetProperty("usagePctText", out var usagePctValue)
                    ? usagePctValue.GetString()?.Trim() ?? "" : "";
                var membership = root.TryGetProperty("membership", out var membershipValue)
                    ? membershipValue.GetString()?.Trim() ?? "" : "";
                _service.SetQwenPageMetadata(reset, membership);
                if (double.TryParse(usagePctText, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var usagePct)
                    && usagePct is >= 0 and <= 100)
                {
                    _service.SetQwen(usagePct, membership, DomesticQuotaService.ParseResetAt(reset));
                    _capturedForNavigation = true;
                    if (_backgroundRefresh) BeginInvoke(Hide);
                    return;
                }
            }
            catch
            {
                // The console shell renders before the subscription card; retry below.
            }
            await Task.Delay(500);
        }
    }

    async Task ShowPendingStatus(DomesticProviderDefinition provider, int generation)
    {
        // Kimi's console now loads its Connect usage service after the main
        // shell. On slower links the old 12-second window hid the background
        // WebView before BillingService/GetUsage completed, leaving both
        // Weekly and 5H stuck on the last successful cache.
        await Task.Delay(TimeSpan.FromSeconds(provider.Id == "qwen" ? 45
            : provider.Id is "kimi" or "minimax" ? 60 : 12));
        if (IsDisposed || generation != _navigationGeneration || _activeProvider != provider
            || _capturedForNavigation) return;
        var snapshot = _service.Snapshot;
        var fetchedAt = provider.Id switch
        {
            "qwen" => snapshot.QwenFetchedAt,
            "xiaomi" => snapshot.XiaomiFetchedAt,
            "kimi" => snapshot.KimiFetchedAt,
            "minimax" => snapshot.MiniMaxFetchedAt,
            _ => null,
        };
        var last = fetchedAt.HasValue
            ? $"最近成功：{fetchedAt.Value.ToLocalTime():M月d日 HH:mm}"
            : "尚无成功记录";
        BeginInvoke(() => _status.Text = $"尚未捕获到{provider.Product}额度响应；{last}。可刷新页面重试。 ");
        if (_backgroundRefresh) BeginInvoke(Hide);
    }

    void CdpResponseReceived(object sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            var requestId = root.GetProperty("requestId").GetString();
            var response = root.GetProperty("response");
            var uri = response.GetProperty("url").GetString() ?? "";
            var mime = response.TryGetProperty("mimeType", out var mimeValue)
                ? mimeValue.GetString() ?? "" : "";
            var resourceType = root.TryGetProperty("type", out var typeValue)
                ? typeValue.GetString() ?? "" : "";
            var statusCode = response.TryGetProperty("status", out var statusValue)
                && statusValue.ValueKind == JsonValueKind.Number
                ? statusValue.GetInt32() : 0;
            var jsonRequest = (resourceType == "XHR" || resourceType == "Fetch")
                && (mime.Contains("json", StringComparison.OrdinalIgnoreCase)
                    || uri.Contains(".json", StringComparison.OrdinalIgnoreCase));
            var isAlibaba = _activeProvider?.Id == "qwen" && jsonRequest
                && Uri.TryCreate(uri, UriKind.Absolute, out var alibabaUri)
                && alibabaUri.Host.Equals("bailian.console.aliyun.com", StringComparison.OrdinalIgnoreCase)
                && alibabaUri.AbsolutePath.Equals("/data/api.json", StringComparison.OrdinalIgnoreCase);
            var isXiaomi = _activeProvider?.Id == "xiaomi"
                && uri.Contains("/api/v1/tokenPlan/usage", StringComparison.OrdinalIgnoreCase);
            var isKimiUsage = _activeProvider?.Id == "kimi"
                && (resourceType == "XHR" || resourceType == "Fetch")
                && IsKimiUsageEndpoint(uri);
            var isKimi = _activeProvider?.Id == "kimi"
                && (jsonRequest || isKimiUsage)
                && uri.Contains("kimi", StringComparison.OrdinalIgnoreCase);
            var isMiniMax = _activeProvider?.Id == "minimax"
                && (jsonRequest || IsMiniMaxUsageEndpoint(uri))
                && IsMiniMaxUsageEndpoint(uri);
            if (statusCode == 429 && (isAlibaba || isXiaomi || isKimi || isMiniMax))
            {
                var providerId = isAlibaba ? "qwen" : isXiaomi ? "xiaomi"
                    : isKimi ? "kimi" : "minimax";
                _service.BackOff(providerId);
                BeginInvoke(() => _status.Text = "供应商额度接口限流，5 分钟后自动重试；当前继续显示最近成功值。");
                if (_backgroundRefresh) BeginInvoke(Hide);
                return;
            }
            if (requestId != null && (isAlibaba || isXiaomi || isKimi || isMiniMax))
            {
                var endpoint = Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                    ? parsed.Host + parsed.AbsolutePath : uri.Split('?')[0];
                lock (_quotaResponses)
                    _quotaResponses[requestId] =
                        (isAlibaba ? "qwen" : isXiaomi ? "xiaomi"
                            : isKimi ? "kimi" : "minimax", endpoint);
                if (isKimiUsage)
                    Console.Error.WriteLine($"[quota] Kimi usage response detected: {endpoint}");
                if (isMiniMax)
                    Console.Error.WriteLine($"[quota] MiniMax usage response detected: {endpoint}");
            }
        }
        catch
        {
            // Ignore unrelated or incomplete browser network events.
        }
    }

    async void CdpLoadingFinished(object sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        string requestId;
        try
        {
            using var finished = JsonDocument.Parse(e.ParameterObjectAsJson);
            requestId = finished.RootElement.GetProperty("requestId").GetString();
        }
        catch { return; }
        (string ProviderId, string Endpoint) responseInfo;
        lock (_quotaResponses)
        {
            if (requestId == null || !_quotaResponses.Remove(requestId, out responseInfo)) return;
        }
        try
        {
            var args = JsonSerializer.Serialize(new { requestId });
            var result = await _web.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Network.getResponseBody", args);
            using var envelope = JsonDocument.Parse(result);
            var body = envelope.RootElement.GetProperty("body").GetString() ?? "";
            if (envelope.RootElement.TryGetProperty("base64Encoded", out var encoded)
                && encoded.ValueKind == JsonValueKind.True)
                body = Encoding.UTF8.GetString(Convert.FromBase64String(body));

            using var doc = JsonDocument.Parse(body);
            double weeklyPct;
            double? fiveHourPct = null;
            string membership = null;
            if (responseInfo.ProviderId == "kimi")
            {
                var kimi = FindKimiUsage(doc.RootElement);
                membership = FindKimiMembership(doc.RootElement);
                if (kimi == null)
                {
                    _service.SetKimiMembership(membership);
                    return;
                }
                weeklyPct = kimi.Value.WeeklyPct;
                fiveHourPct = kimi.Value.FiveHourPct;
                _service.SetKimi(weeklyPct, fiveHourPct, membership,
                    kimi.Value.WeeklyResetAt, kimi.Value.FiveHourResetAt);
                Console.Error.WriteLine(
                    $"[quota] Kimi updated: Weekly {weeklyPct:0.##}%, 5H "
                    + (fiveHourPct.HasValue ? $"{fiveHourPct.Value:0.##}%" : "--"));
            }
            else if (responseInfo.ProviderId == "minimax")
            {
                var miniMax = FindMiniMaxUsage(doc.RootElement);
                if (!miniMax.HasValue) return;
                weeklyPct = miniMax.Value.WeeklyPct;
                fiveHourPct = miniMax.Value.FiveHourPct;
                membership = miniMax.Value.Membership;
                _service.SetMiniMax(weeklyPct, fiveHourPct, membership,
                    miniMax.Value.WeeklyResetAt, miniMax.Value.FiveHourResetAt);
                Console.Error.WriteLine(
                    $"[quota] MiniMax updated: Weekly {weeklyPct:0.##}%, 5H "
                    + (fiveHourPct.HasValue ? $"{fiveHourPct.Value:0.##}%" : "--"));
            }
            else
            {
                if (responseInfo.ProviderId == "qwen")
                    membership = DomesticQuotaService.QwenSubscriptionName;
                var isQwen = responseInfo.ProviderId == "qwen";
                var qwen = isQwen
                    ? FindQwenUsage(doc.RootElement) : null;
                var pct = qwen?.Pct ?? FindUsage(doc.RootElement, isQwen);
                if (!pct.HasValue) return;
                weeklyPct = pct.Value;
                if (responseInfo.ProviderId == "qwen")
                    _service.SetQwen(pct.Value, membership, qwen?.ResetAt);
                else _service.SetXiaomi(pct.Value);
            }
            var responseProvider = ProviderById(responseInfo.ProviderId);
            var loginSaved = responseInfo.ProviderId is "qwen" or "kimi" or "minimax"
                && await PersistLoginCookies(responseProvider.Url);
            _capturedForNavigation = true;
            BeginInvoke(() => _status.Text =
                responseInfo.ProviderId == "kimi"
                    ? $"已取得 Kimi 准确用量：Weekly {weeklyPct:0.##}%"
                        + (fiveHourPct.HasValue ? $"，5h {fiveHourPct.Value:0.##}%" : "")
                        + "（已缓存）" + (loginSaved ? "；登录状态已持久保存" : "")
                    : responseInfo.ProviderId == "minimax"
                    ? $"已取得 MiniMax 准确用量：Weekly {weeklyPct:0.##}%"
                        + (fiveHourPct.HasValue ? $"，5h {fiveHourPct.Value:0.##}%" : "")
                        + "（已缓存）" + (loginSaved ? "；登录状态已持久保存" : "")
                    : $"已取得{(responseInfo.ProviderId == "qwen" ? "阿里云" : "小米")}准确用量：{weeklyPct:F1}%（已缓存）"
                    + (responseInfo.ProviderId == "qwen" && !string.IsNullOrWhiteSpace(membership)
                        ? $"；订阅：{membership}" : "")
                    + (loginSaved ? "；登录状态已持久保存" : ""));
            if (_backgroundRefresh) BeginInvoke(Hide);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[quota] {responseInfo.ProviderId} response parse failed at {responseInfo.Endpoint}: {ex.Message}");
            BeginInvoke(() => _status.Text = $"额度响应读取失败：{ex.Message}");
        }
    }

    static bool IsKimiUsageEndpoint(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        return parsed.AbsolutePath.Contains("BillingService/GetUsage",
            StringComparison.OrdinalIgnoreCase);
    }

    static bool IsMiniMaxUsageEndpoint(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        var host = parsed.Host;
        if (!host.Contains("minimax", StringComparison.OrdinalIgnoreCase)
            && !host.Contains("minimaxi", StringComparison.OrdinalIgnoreCase))
            return false;
        var path = parsed.AbsolutePath;
        return path.Contains("token_plan", StringComparison.OrdinalIgnoreCase)
            || path.Contains("coding_plan", StringComparison.OrdinalIgnoreCase)
            || path.Contains("model_remains", StringComparison.OrdinalIgnoreCase)
            || path.Contains("usage", StringComparison.OrdinalIgnoreCase)
            || path.Contains("remains", StringComparison.OrdinalIgnoreCase);
    }

    async Task<bool> PersistLoginCookies(string url)
    {
        if (_web.CoreWebView2 == null) return false;
        try
        {
            var manager = _web.CoreWebView2.CookieManager;
            var cookies = await manager.GetCookiesAsync(url);
            var expires = DateTime.UtcNow.Add(LoginRetention);
            var updated = false;
            foreach (var cookie in cookies)
            {
                if (!cookie.IsSession) continue;
                cookie.Expires = expires;
                manager.AddOrUpdateCookie(cookie);
                updated = true;
            }
            return updated || cookies.Any(cookie => !cookie.IsSession && cookie.Expires > DateTime.UtcNow);
        }
        catch
        {
            return false;
        }
    }

    internal readonly record struct MiniMaxUsage(double WeeklyPct, double? FiveHourPct,
        string Membership, DateTimeOffset? WeeklyResetAt, DateTimeOffset? FiveHourResetAt);
    readonly record struct KimiUsage(double WeeklyPct, double? FiveHourPct,
        DateTimeOffset? WeeklyResetAt, DateTimeOffset? FiveHourResetAt);
    readonly record struct QwenUsage(double Pct, DateTimeOffset? ResetAt);

    static QwenUsage? FindQwenUsage(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var values = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value,
                StringComparer.OrdinalIgnoreCase);
            if (Number(values, "TotalValue") is double total && total > 0
                && (Number(values, "TotalSurplusValue") ?? Number(values, "SurplusValue")) is double remain)
            {
                DateTimeOffset? resetAt = null;
                foreach (var name in new[]
                         {
                             "ResetTime", "NextResetTime", "ResetAt", "NextResetAt",
                             "EndTime", "ExpireTime", "ExpirationTime", "ValidTo", "CycleEndTime"
                         })
                {
                    if (!values.TryGetValue(name, out var value)) continue;
                    resetAt = DateTimeValue(value);
                    if (resetAt.HasValue) break;
                }
                return new QwenUsage(100.0 * Math.Max(0, total - remain) / total, resetAt);
            }
            foreach (var child in values.Values)
                if (FindQwenUsage(child) is QwenUsage found) return found;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (FindQwenUsage(child) is QwenUsage found) return found;
        }
        return null;
    }

    static DateTimeOffset? DateTimeValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return DomesticQuotaService.ParseResetAt(value.GetString());
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var epoch)) return null;
        try
        {
            if (epoch > 10_000_000_000) return DateTimeOffset.FromUnixTimeMilliseconds(epoch);
            if (epoch > 1_000_000_000) return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }

    static DateTimeOffset? FindDirectResetAt(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            if (!name.Contains("reset") && !name.Contains("refresh")
                && !name.Contains("expire") && !name.Contains("expiry")
                && !name.Contains("end_time") && !name.Contains("endtime")) continue;
            if (DateTimeValue(property.Value) is DateTimeOffset absolute)
                return absolute;
            if (RelativeResetAt(name, property.Value) is DateTimeOffset relative)
                return relative;
        }
        return null;
    }

    static DateTimeOffset? FindResetAt(JsonElement element)
    {
        if (FindDirectResetAt(element) is DateTimeOffset direct) return direct;
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;
            if (FindResetAt(property.Value) is DateTimeOffset nested) return nested;
        }
        return null;
    }

    static DateTimeOffset? RelativeResetAt(string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return DomesticQuotaService.ParseResetAt(value.GetString());
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var amount)
            || amount <= 0) return null;
        double seconds;
        if (name.Contains("millisecond") || name.EndsWith("_ms")) seconds = amount / 1000.0;
        else if (name.Contains("minute") || name.EndsWith("_min")) seconds = amount * 60.0;
        else if (name.Contains("hour")) seconds = amount * 3600.0;
        else seconds = amount;
        return seconds <= 31 * 24 * 3600 ? DateTimeOffset.Now.AddSeconds(seconds) : null;
    }

    static string FindKimiMembership(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return "";
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && (property.Name.Contains("membership", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("member_level", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("plan_name", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("tier", StringComparison.OrdinalIgnoreCase)))
                {
                    var value = property.Value.GetString()?.Trim() ?? "";
                    if (value.Length is > 0 and <= 32)
                    {
                        if (value.StartsWith("LEVEL_", StringComparison.OrdinalIgnoreCase)) continue;
                        return value;
                    }
                }
                var nested = FindKimiMembership(property.Value);
                if (nested.Length > 0) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var nested = FindKimiMembership(child);
                if (nested.Length > 0) return nested;
            }
        }
        return "";
    }

    static KimiUsage? FindKimiUsage(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var values = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value,
                StringComparer.OrdinalIgnoreCase);
            if (values.TryGetValue("usages", out var usages) && usages.ValueKind == JsonValueKind.Array)
            {
                foreach (var usage in usages.EnumerateArray())
                {
                    if (usage.ValueKind != JsonValueKind.Object) continue;
                    var usageValues = usage.EnumerateObject().ToDictionary(p => p.Name, p => p.Value,
                        StringComparer.OrdinalIgnoreCase);
                    if (!usageValues.TryGetValue("detail", out var detail)) continue;
                    var weeklyPct = PercentageFromRemaining(detail);
                    if (!weeklyPct.HasValue) continue;
                    var weeklyResetAt = FindResetAt(detail) ?? FindDirectResetAt(usage);
                    double? fiveHourPct = null;
                    DateTimeOffset? fiveHourResetAt = null;
                    if (usageValues.TryGetValue("limits", out var limits)
                        && limits.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var limit in limits.EnumerateArray())
                        {
                            if (limit.ValueKind != JsonValueKind.Object) continue;
                            var limitValues = limit.EnumerateObject().ToDictionary(p => p.Name, p => p.Value,
                                StringComparer.OrdinalIgnoreCase);
                            if (limitValues.TryGetValue("detail", out var rateDetail)
                                && PercentageFromRemaining(rateDetail) is double ratePct)
                            {
                                fiveHourPct = ratePct;
                                fiveHourResetAt = FindResetAt(rateDetail) ?? FindDirectResetAt(limit);
                                break;
                            }
                        }
                    }
                    return new KimiUsage(weeklyPct.Value, fiveHourPct,
                        weeklyResetAt, fiveHourResetAt);
                }
            }
            foreach (var child in values.Values)
                if (FindKimiUsage(child) is KimiUsage found) return found;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (FindKimiUsage(child) is KimiUsage found) return found;
        }
        return null;
    }

    internal static MiniMaxUsage? FindMiniMaxUsage(JsonElement element)
    {
        var candidates = new List<(MiniMaxUsage Usage, int Score)>();
        CollectMiniMaxUsage(element, candidates);
        return candidates.Count == 0
            ? null
            : candidates.OrderByDescending(item => item.Score).First().Usage;
    }

    static void CollectMiniMaxUsage(JsonElement element, List<(MiniMaxUsage Usage, int Score)> candidates)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var values = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value,
                StringComparer.OrdinalIgnoreCase);
            if (TryMiniMaxUsage(values) is (MiniMaxUsage Usage, int Score) candidate)
                candidates.Add(candidate);
            foreach (var child in values.Values)
                if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    CollectMiniMaxUsage(child, candidates);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                CollectMiniMaxUsage(child, candidates);
        }
    }

    static (MiniMaxUsage Usage, int Score)? TryMiniMaxUsage(Dictionary<string, JsonElement> values)
    {
        var weeklyPct = MiniMaxPercent(values, "current_weekly")
            ?? MiniMaxPercent(values, "weekly");
        var fiveHourPct = MiniMaxPercent(values, "current_interval")
            ?? MiniMaxPercent(values, "interval")
            ?? MiniMaxPercent(values, "five_hour")
            ?? MiniMaxPercent(values, "fiveHour");
        if (!weeklyPct.HasValue) return null;

        var weeklyResetAt = MiniMaxResetAt(values, "weekly_remains_time")
            ?? MiniMaxResetAt(values, "current_weekly_remains_time")
            ?? FindDirectResetAt(ObjectFromValues(values, "weekly"));
        var fiveHourResetAt = MiniMaxResetAt(values, "remains_time")
            ?? MiniMaxResetAt(values, "current_interval_remains_time")
            ?? MiniMaxResetAt(values, "interval_remains_time");
        var membership = FindMiniMaxMembership(values);
        var score = 0;
        if (values.TryGetValue("model_name", out var modelValue)
            && modelValue.ValueKind == JsonValueKind.String)
        {
            var model = modelValue.GetString() ?? "";
            if (model.Equals("general", StringComparison.OrdinalIgnoreCase)) score += 30;
            if (!model.Contains("video", StringComparison.OrdinalIgnoreCase)
                && !model.Contains("hailuo", StringComparison.OrdinalIgnoreCase)) score += 15;
        }
        if (fiveHourPct.HasValue) score += 10;
        if (membership.Length > 0) score += 5;
        return (new MiniMaxUsage(weeklyPct.Value, fiveHourPct, membership,
            weeklyResetAt, fiveHourResetAt), score);
    }

    static JsonElement ObjectFromValues(Dictionary<string, JsonElement> values, string name)
    {
        return values.TryGetValue(name, out var value) ? value : default;
    }

    static DateTimeOffset? MiniMaxResetAt(Dictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String)
            return DomesticQuotaService.ParseResetAt(value.GetString());
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds)
            && seconds > 0 && seconds <= 31 * 24 * 3600)
            return DateTimeOffset.Now.AddSeconds(seconds);
        return DateTimeValue(value);
    }

    static string FindMiniMaxMembership(Dictionary<string, JsonElement> values)
    {
        foreach (var name in new[]
                 {
                     "combo_name", "plan_name", "plan_title", "current_subscribe_title",
                     "subscribe_title", "subscription_name", "title"
                 })
        {
            if (!values.TryGetValue(name, out var value)
                || value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString()?.Trim() ?? "";
            if (text.Length is > 0 and <= 48) return text;
        }
        return "TOKEN PLAN";
    }

    static double? MiniMaxPercent(Dictionary<string, JsonElement> values, string prefix)
    {
        foreach (var name in new[]
                 {
                     $"{prefix}_used_percent", $"{prefix}_usage_percent",
                     $"{prefix}_usagePercent", $"{prefix}_usedPercent"
                 })
            if (PercentNumber(values, name) is double used) return used;
        foreach (var name in new[]
                 {
                     $"{prefix}_remaining_percent", $"{prefix}_remains_percent",
                     $"{prefix}_remainingPercent", $"{prefix}_remainsPercent"
                 })
            if (PercentNumber(values, name) is double remaining) return 100 - remaining;

        var total = Number(values, $"{prefix}_total_count")
            ?? Number(values, $"{prefix}_total");
        var usedCount = Number(values, $"{prefix}_usage_count")
            ?? Number(values, $"{prefix}_used_count")
            ?? Number(values, $"{prefix}_used");
        var remainingCount = Number(values, $"{prefix}_remains_count")
            ?? Number(values, $"{prefix}_remaining_count")
            ?? Number(values, $"{prefix}_remaining");
        if (total is > 0)
        {
            if (usedCount.HasValue) return 100 * usedCount.Value / total.Value;
            if (remainingCount.HasValue) return 100 * (total.Value - remainingCount.Value) / total.Value;
        }
        return null;
    }

    static double? PercentageFromRemaining(JsonElement detail)
    {
        if (detail.ValueKind != JsonValueKind.Object) return null;
        var values = detail.EnumerateObject().ToDictionary(p => p.Name, p => p.Value,
            StringComparer.OrdinalIgnoreCase);
        var remaining = Number(values, "remaining");
        var limit = Number(values, "limit");
        return remaining.HasValue && limit is > 0
            ? 100.0 * Math.Max(0, limit.Value - remaining.Value) / limit.Value : null;
    }

    static double? FindUsage(JsonElement element, bool alibaba)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var values = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value,
                StringComparer.OrdinalIgnoreCase);
            if (alibaba && Number(values, "TotalValue") is double total && total > 0
                && (Number(values, "TotalSurplusValue") ?? Number(values, "SurplusValue")) is double remain)
                return 100.0 * Math.Max(0, total - remain) / total;
            foreach (var name in new[] { "usagePercent", "usedPercent", "usageRate", "usedRate", "ratio" })
                if (Number(values, name) is double direct)
                    return name.EndsWith("Rate", StringComparison.OrdinalIgnoreCase) || name == "ratio"
                        ? direct * 100 : direct;
            var totalAny = Number(values, "totalCredits") ?? Number(values, "total") ?? Number(values, "quota");
            var used = Number(values, "usedCredits") ?? Number(values, "used");
            var remaining = Number(values, "remainingCredits") ?? Number(values, "remaining");
            if (totalAny is > 0)
            {
                if (used.HasValue) return 100 * used.Value / totalAny.Value;
                if (remaining.HasValue) return 100 * (totalAny.Value - remaining.Value) / totalAny.Value;
            }
            foreach (var child in values.Values)
                if (FindUsage(child, alibaba) is double found) return found;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (FindUsage(child, alibaba) is double found) return found;
        }
        return null;
    }

    static double? Number(Dictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    static double? PercentNumber(Dictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = (value.GetString() ?? "").Trim().TrimEnd('%');
        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;
    }
}
