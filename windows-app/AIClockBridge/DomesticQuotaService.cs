using System.Globalization;
using System.Text;
using System.Text.Json;
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
        new("minimax", "MiniMax", "MiniMax 开放平台", "https://platform.minimaxi.com/", false),
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
    public double? XiaomiPlanPct;
    public double? KimiWeeklyPct;
    public double? KimiFiveHourPct;
    public DateTime? QwenFetchedAt;
    public DateTime? XiaomiFetchedAt;
    public DateTime? KimiFetchedAt;
}

/// Reads the same read-only quota responses as the vendors' own account pages.
/// Passwords never pass through the app: sign-in happens inside the vendor page
/// and WebView2 owns its isolated persistent browser profile.
sealed class DomesticQuotaService
{
    static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIClockBridge", "domestic-quota-cache.json");
    readonly object _lock = new();
    DomesticQuotaSnapshot _snapshot = Load();
    DomesticQuotaAuthForm _form;

    public DomesticQuotaSnapshot Snapshot
    {
        get
        {
            lock (_lock) return new DomesticQuotaSnapshot
            {
                QwenPlanPct = _snapshot.QwenPlanPct,
                XiaomiPlanPct = _snapshot.XiaomiPlanPct,
                KimiWeeklyPct = _snapshot.KimiWeeklyPct,
                KimiFiveHourPct = _snapshot.KimiFiveHourPct,
                QwenFetchedAt = _snapshot.QwenFetchedAt,
                XiaomiFetchedAt = _snapshot.XiaomiFetchedAt,
                KimiFetchedAt = _snapshot.KimiFetchedAt,
            };
        }
    }

    public void OpenAuthorization(string providerId = "qwen")
    {
        if (_form == null || _form.IsDisposed)
            _form = new DomesticQuotaAuthForm(this, initialProviderId: providerId);
        else
            _form.NavigateToProvider(providerId);
        _form.TopMost = true;
        if (!_form.Visible) _form.Show();
        _form.BringToFront();
        _form.Activate();
        _form.BeginInvoke(() => _form.TopMost = false);
    }

    internal void SetQwen(double pct)
    {
        lock (_lock) { _snapshot.QwenPlanPct = Clamp(pct); _snapshot.QwenFetchedAt = DateTime.UtcNow; Save(); }
    }

    internal void SetXiaomi(double pct)
    {
        lock (_lock) { _snapshot.XiaomiPlanPct = Clamp(pct); _snapshot.XiaomiFetchedAt = DateTime.UtcNow; Save(); }
    }

    internal void SetKimi(double weeklyPct, double? fiveHourPct)
    {
        lock (_lock)
        {
            _snapshot.KimiWeeklyPct = Clamp(weeklyPct);
            _snapshot.KimiFiveHourPct = fiveHourPct.HasValue ? Clamp(fiveHourPct.Value) : null;
            _snapshot.KimiFetchedAt = DateTime.UtcNow;
            Save();
        }
    }

    static double Clamp(double value) => Math.Clamp(value, 0, 100);

    static DomesticQuotaSnapshot Load()
    {
        try { return JsonSerializer.Deserialize<DomesticQuotaSnapshot>(File.ReadAllText(CachePath),
            new JsonSerializerOptions { IncludeFields = true }) ?? new(); }
        catch { return new(); }
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
        content.Controls.Add(_web);
        content.Controls.Add(_status);
        content.Controls.Add(header);
        Controls.Add(content);
        Controls.Add(navigation);

        Shown += async (_, _) => await SelectProvider(ProviderById(_initialProviderId));
    }

    public void NavigateToProvider(string providerId)
    {
        _initialProviderId = providerId;
        if (IsHandleCreated)
            BeginInvoke(async () => await SelectProvider(ProviderById(providerId)));
    }

    static DomesticProviderDefinition ProviderById(string providerId) =>
        DomesticProviderCatalog.All.FirstOrDefault(x => x.Id == providerId)
        ?? DomesticProviderCatalog.All[0];

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_hideOnUserClose && e.CloseReason == CloseReason.UserClosing)
        {
            if (_activeProvider?.Id is "qwen" or "kimi") _ = PersistLoginCookies(_activeProvider.Url);
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
        _providerState.Text = provider.CaptureSupported
            ? "已支持准确额度读取：登录后进入订阅/用量页面即可自动捕获"
            : "已列入厂商目录：可以登录控制台，准确额度读取规则尚待适配";
        _providerState.ForeColor = provider.CaptureSupported
            ? Color.FromArgb(22, 101, 52) : Color.FromArgb(180, 83, 9);
        await Navigate(provider);
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
                if (e.IsSuccess && _activeProvider?.Id is "qwen" or "kimi")
                    await PersistLoginCookies(_activeProvider.Url);
            };
        }
        _status.Text = provider.CaptureSupported
            ? $"请登录{provider.Name}；捕获到准确用量后会自动缓存百分比。"
            : $"{provider.Name}已提供统一登录入口；当前版本暂不读取其额度数字。";
        _web.CoreWebView2.Navigate(provider.Url);
        if (provider.CaptureSupported) _ = ShowPendingStatus(provider, generation);
    }

    async Task ShowPendingStatus(DomesticProviderDefinition provider, int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (IsDisposed || generation != _navigationGeneration || _activeProvider != provider
            || _capturedForNavigation) return;
        var snapshot = _service.Snapshot;
        var fetchedAt = provider.Id switch
        {
            "qwen" => snapshot.QwenFetchedAt,
            "xiaomi" => snapshot.XiaomiFetchedAt,
            "kimi" => snapshot.KimiFetchedAt,
            _ => null,
        };
        var last = fetchedAt.HasValue
            ? $"最近成功：{fetchedAt.Value.ToLocalTime():M月d日 HH:mm}"
            : "尚无成功记录";
        BeginInvoke(() => _status.Text = $"尚未捕获到{provider.Product}额度响应；{last}。可刷新页面重试。 ");
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
            var jsonRequest = (resourceType == "XHR" || resourceType == "Fetch")
                && (mime.Contains("json", StringComparison.OrdinalIgnoreCase)
                    || uri.Contains(".json", StringComparison.OrdinalIgnoreCase));
            var isAlibaba = _activeProvider?.Id == "qwen" && jsonRequest
                && Uri.TryCreate(uri, UriKind.Absolute, out var alibabaUri)
                && alibabaUri.Host.Equals("bailian.console.aliyun.com", StringComparison.OrdinalIgnoreCase)
                && alibabaUri.AbsolutePath.Equals("/data/api.json", StringComparison.OrdinalIgnoreCase);
            var isXiaomi = _activeProvider?.Id == "xiaomi"
                && uri.Contains("/api/v1/tokenPlan/usage", StringComparison.OrdinalIgnoreCase);
            var isKimi = _activeProvider?.Id == "kimi" && jsonRequest
                && uri.Contains("kimi.gateway.billing.v1.BillingService/GetUsages",
                    StringComparison.OrdinalIgnoreCase);
            if (requestId != null && (isAlibaba || isXiaomi || isKimi))
            {
                var endpoint = Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                    ? parsed.Host + parsed.AbsolutePath : uri.Split('?')[0];
                _quotaResponses[requestId] =
                    (isAlibaba ? "qwen" : isXiaomi ? "xiaomi" : "kimi", endpoint);
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
        if (requestId == null || !_quotaResponses.Remove(requestId, out var responseInfo)) return;
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
            if (responseInfo.ProviderId == "kimi")
            {
                var kimi = FindKimiUsage(doc.RootElement);
                if (kimi == null) return;
                weeklyPct = kimi.Value.WeeklyPct;
                fiveHourPct = kimi.Value.FiveHourPct;
                _service.SetKimi(weeklyPct, fiveHourPct);
            }
            else
            {
                var pct = FindUsage(doc.RootElement, responseInfo.ProviderId == "qwen");
                if (!pct.HasValue) return;
                weeklyPct = pct.Value;
                if (responseInfo.ProviderId == "qwen") _service.SetQwen(pct.Value);
                else _service.SetXiaomi(pct.Value);
            }
            var responseProvider = ProviderById(responseInfo.ProviderId);
            var loginSaved = responseInfo.ProviderId is "qwen" or "kimi"
                && await PersistLoginCookies(responseProvider.Url);
            _capturedForNavigation = true;
            BeginInvoke(() => _status.Text =
                responseInfo.ProviderId == "kimi"
                    ? $"已取得 Kimi 准确用量：Weekly {weeklyPct:0.##}%"
                        + (fiveHourPct.HasValue ? $"，5h {fiveHourPct.Value:0.##}%" : "")
                        + "（已缓存）" + (loginSaved ? "；登录状态已持久保存" : "")
                    : $"已取得{(responseInfo.ProviderId == "qwen" ? "阿里云" : "小米")}准确用量：{weeklyPct:F1}%（已缓存）"
                    + (loginSaved ? "；登录状态已持久保存" : ""));
        }
        catch (Exception ex)
        {
            BeginInvoke(() => _status.Text = $"额度响应读取失败：{ex.Message}");
        }
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

    readonly record struct KimiUsage(double WeeklyPct, double? FiveHourPct);

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
                    double? fiveHourPct = null;
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
                                break;
                            }
                        }
                    }
                    return new KimiUsage(weeklyPct.Value, fiveHourPct);
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
}
