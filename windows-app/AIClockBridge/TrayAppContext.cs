using System.Diagnostics;
using System.Drawing;

namespace AIClockBridge;

// Tray icon: the retro Macintosh device logo. Left click opens a live mirror
// of the ESP8266 screen (MirrorForm); right click opens the control menu with
// usage meters and device remote control. No quota text lives in the tray
// itself.
sealed class TrayAppContext : ApplicationContext
{
    const string CycleEnabledKey = "display_cycle_enabled";
    const string CyclePagesKey = "display_cycle_pages";
    const string CycleIntervalKey = "display_cycle_interval_seconds";
    const string DomesticProviderKey = "domestic_provider";
    const string ScreenSaverTimeoutKey = "screensaver_timeout_minutes";
    const string ScreenSaverPreviousModeKey = "screensaver_previous_mode";
    static readonly (string Title, string Mode)[] DisplayModes =
    {
        ("智能跟随", "auto"), ("Claude", "claude"),
        ("Codex", "codex"), ("Claude + Codex 额度", "dual"), ("国产模型", "domestic"),
        ("系统监控", "net"), ("音乐播放", "music"), ("股票行情", "stock"),
        ("天气时钟", "weather"),
    };
    static readonly (string Title, string Mode)[] CycleModes =
    {
        ("Claude + Codex 额度", "dual"), ("Codex", "codex"), ("Claude", "claude"), ("天气时钟", "weather"),
        ("股票行情", "stock"), ("国产模型", "domestic"), ("音乐播放", "music"),
        ("系统监控", "net"),
    };
    static readonly string[] DefaultCycleModes = { "codex", "claude", "weather", "stock" };

    readonly NotifyIcon _trayIcon;
    readonly StatusService _service;
    readonly UsageFetcher _usage;
    readonly DomesticQuotaService _domesticUsage;
    readonly NowPlayingMonitor _nowPlaying;
    readonly StockMonitor _stocks;
    readonly WeatherMonitor _weather;
    readonly int _port;
    readonly MirrorForm _mirror;
    readonly ContextMenuStrip _menu = new();

    readonly ToolStripMenuItem _claudeUsageItem = new("Claude …") { Enabled = false };
    readonly ToolStripMenuItem _codexUsageItem = new("Codex …") { Enabled = false };
    readonly ToolStripMenuItem _deviceInfoItem = new("设备：未设置") { Enabled = false };
    readonly ToolStripMenuItem _startupItem = new("随 Windows 启动") { CheckOnClick = false };
    readonly Dictionary<string, ToolStripMenuItem> _modeItems = new();
    readonly Dictionary<string, ToolStripMenuItem> _domesticProviderItems = new();
    readonly ToolStripMenuItem _cycleEnabledItem = new("启用循环展示");
    readonly Dictionary<string, ToolStripMenuItem> _cyclePageItems = new();
    readonly Dictionary<int, ToolStripMenuItem> _cycleIntervalItems = new();
    readonly System.Windows.Forms.Timer _cycleTimer = new();
    readonly System.Windows.Forms.Timer _screenSaverTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _domesticRefreshTimer = new() { Interval = 120 * 1000 };
    readonly Dictionary<int, ToolStripMenuItem> _screenSaverTimeoutItems = new();
    ToolStripMenuItem _screenSaverMenu;
    bool _cycleEnabled;
    bool _cycleBusy;
    bool _keepMenuOpen;
    int _cycleIntervalSeconds;
    int _cycleIndex = -1;
    int _screenSaverTimeoutMinutes;
    bool _screenSaverActive;
    bool _screenSaverBusy;
    bool _screenSaverPreviewActive;
    string _modeBeforeScreenSaver = "auto";
    string _lastKnownMode = "auto";
    string _domesticProvider = "qwen";
    DateTime _lastScreenSaverActivityAt = DateTime.UtcNow;
    DateTime _ignoreScreenSaverWakeUntil = DateTime.MinValue;

    public TrayAppContext(StatusService service, UsageFetcher usage, DomesticQuotaService domesticUsage,
                          NetSpeedMonitor netMonitor,
                          NowPlayingMonitor nowPlaying, StockMonitor stocks, WeatherMonitor weather, int port)
    {
        _service = service;
        _usage = usage;
        _domesticUsage = domesticUsage;
        _nowPlaying = nowPlaying;
        _stocks = stocks;
        _weather = weather;
        _port = port;
        _mirror = new MirrorForm(service, netMonitor, nowPlaying, stocks, weather);

        LoadCycleSettings();
        LoadDomesticProviderSettings();
        LoadScreenSaverSettings();
        _cycleTimer.Tick += async (_, _) => await AdvanceCycle();
        _screenSaverTimer.Tick += async (_, _) => await ScreenSaverTick();
        _domesticRefreshTimer.Tick += (_, _) => _domesticUsage.Refresh(_domesticProvider);

        BuildMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = TrayIconFromAsset(),
            Text = "AI Clock Bridge",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _mirror.Toggle();
        };
        _menu.Opening += (_, _) =>
        {
            _startupItem.Checked = StartupManager.IsEnabled;
            _usage.Refresh();
            _domesticUsage.Refresh(_domesticProvider);
            RefreshUsageLines();
            _ = RefreshDeviceSection();
        };
        _usage.OnUpdate = RefreshUsageLines;
        if (_cycleEnabled)
        {
            _cycleTimer.Start();
            _ = AdvanceCycle();
        }
        _screenSaverTimer.Start();
        _domesticUsage.Refresh(_domesticProvider);
        _domesticRefreshTimer.Start();
        _ = RecoverScreenSaverState();
    }

    /// User-supplied device logo (bezel + dark screen + smiley + green status
    /// dot). Full-color, matching the Mac menu-bar icon.
    static Icon TrayIconFromAsset()
    {
        using var bmp = new Bitmap(MirrorControl.LoadAsset("happy-mac.png"),
                                   new Size(32, 32));
        var handle = bmp.GetHicon();
        // clone so the icon owns its data; the GetHicon handle would leak
        // otherwise but a single tray icon for the app lifetime is fine
        return Icon.FromHandle(handle);
    }

    // MARK: - menu construction

    void BuildMenu()
    {
        var quotaMenu = new ToolStripMenuItem("模型额度");
        quotaMenu.DropDownItems.Add(_claudeUsageItem);
        quotaMenu.DropDownItems.Add(_codexUsageItem);
        quotaMenu.DropDownItems.Add(new ToolStripSeparator());
        var domesticAuthorization = new ToolStripMenuItem("国产模型额度授权");
        domesticAuthorization.Click += (_, _) =>
            _menu.BeginInvoke(() => _domesticUsage.OpenAuthorization(_domesticProvider));
        quotaMenu.DropDownItems.Add(domesticAuthorization);
        _menu.Items.Add(quotaMenu);

        var deviceMenu = new ToolStripMenuItem("设备连接");
        deviceMenu.DropDownItems.Add(_deviceInfoItem);
        deviceMenu.DropDownItems.Add(new ToolStripSeparator());
        deviceMenu.DropDownItems.Add(MakeItem("自动查找并配对", async (_, _) => await AutoPairAction()));
        deviceMenu.DropDownItems.Add(MakeItem("设置设备地址", (_, _) => SetDeviceAddress()));
        deviceMenu.DropDownItems.Add(MakeItem("打开设备网页", (_, _) => OpenDevicePage()));
        deviceMenu.DropDownItems.Add(MakeItem("将本机设为桥接", async (_, _) => await PointBridgeHere()));
        _menu.Items.Add(deviceMenu);

        var displayMenu = new ToolStripMenuItem("显示模式");
        foreach (var (title, mode) in DisplayModes)
        {
            if (mode == "domestic")
            {
                var domesticMenu = new ToolStripMenuItem(title);
                foreach (var provider in DomesticProviderCatalog.All)
                {
                    var providerTitle = provider.CaptureSupported
                        ? provider.Name : $"{provider.Name}（待接）";
                    var providerItem = new ToolStripMenuItem(providerTitle);
                    providerItem.Click += async (_, _) => await SetDomesticProvider(provider.Id);
                    KeepOpenOnClick(providerItem);
                    _domesticProviderItems[provider.Id] = providerItem;
                    domesticMenu.DropDownItems.Add(providerItem);
                }
                KeepOpenWhileSetting(domesticMenu.DropDown);
                _modeItems[mode] = domesticMenu;
                displayMenu.DropDownItems.Add(domesticMenu);
                UpdateDomesticProviderMenu();
                continue;
            }
            var item = new ToolStripMenuItem(title);
            item.Click += async (_, _) => await SetDisplayMode(mode);
            _modeItems[mode] = item;
            displayMenu.DropDownItems.Add(item);
        }
        displayMenu.DropDownItems.Add(new ToolStripSeparator());
        _screenSaverMenu = new ToolStripMenuItem();
        foreach (var (title, minutes) in new[]
        {
            ("关闭", 0), ("1 分钟", 1), ("5 分钟", 5), ("10 分钟", 10),
            ("30 分钟", 30), ("60 分钟", 60),
        })
        {
            var item = new ToolStripMenuItem(title);
            item.Click += async (_, _) => await SetScreenSaverTimeout(minutes);
            KeepOpenOnClick(item);
            _screenSaverTimeoutItems[minutes] = item;
            _screenSaverMenu.DropDownItems.Add(item);
        }
        _screenSaverMenu.DropDownItems.Add(new ToolStripSeparator());
        _screenSaverMenu.DropDownItems.Add(MakeItem("立即预览", async (_, _) => await EnterScreenSaver(preview: true)));
        KeepOpenWhileSetting(_screenSaverMenu.DropDown);
        displayMenu.DropDownItems.Add(_screenSaverMenu);
        _menu.Items.Add(displayMenu);
        UpdateScreenSaverMenu();

        var cycleMenu = new ToolStripMenuItem("循环展示");
        _cycleEnabledItem.Click += async (_, _) => await SetCycleEnabled(!_cycleEnabled, restoreAuto: true);
        KeepOpenOnClick(_cycleEnabledItem);
        cycleMenu.DropDownItems.Add(_cycleEnabledItem);
        var cyclePagesMenu = new ToolStripMenuItem("循环页面");
        foreach (var (title, mode) in CycleModes)
        {
            var item = new ToolStripMenuItem(title) { CheckOnClick = true, Checked = CyclePages().Contains(mode) };
            item.CheckedChanged += (_, _) => SaveCyclePages();
            KeepOpenOnClick(item);
            _cyclePageItems[mode] = item;
            cyclePagesMenu.DropDownItems.Add(item);
        }
        cycleMenu.DropDownItems.Add(cyclePagesMenu);
        var intervalMenu = new ToolStripMenuItem("切换间隔");
        foreach (var seconds in new[] { 10, 15, 30, 60 })
        {
            var item = new ToolStripMenuItem($"每 {seconds} 秒") { Checked = seconds == _cycleIntervalSeconds };
            item.Click += (_, _) => SetCycleInterval(seconds);
            KeepOpenOnClick(item);
            _cycleIntervalItems[seconds] = item;
            intervalMenu.DropDownItems.Add(item);
        }
        cycleMenu.DropDownItems.Add(intervalMenu);
        cycleMenu.DropDownItems.Add(MakeItem("调整展示顺序", (_, _) =>
            _menu.BeginInvoke(EditCycleOrder)));
        KeepOpenWhileSetting(_menu);
        KeepOpenWhileSetting(cycleMenu.DropDown);
        KeepOpenWhileSetting(cyclePagesMenu.DropDown);
        KeepOpenWhileSetting(intervalMenu.DropDown);
        _menu.Items.Add(cycleMenu);
        UpdateCycleMenu();

        var contentMenu = new ToolStripMenuItem("内容设置");
        contentMenu.DropDownItems.Add(MakeItem("设置自选股", (_, _) =>
            _menu.BeginInvoke(OpenStockSettings)));
        var weatherMenu = new ToolStripMenuItem("设置天气");
        weatherMenu.DropDownItems.Add(MakeItem("数据源与定位", (_, _) =>
            _menu.BeginInvoke(OpenWeatherSettings)));
        var weatherAnimationMenu = new ToolStripMenuItem("右下角动画");
        foreach (var (title, animation) in new[]
        {
            ("天气机器人", "robot"), ("像素天气小屋", "house"),
            ("像素盆栽", "plant"), ("天气萌宠", "pet"), ("关闭动画", "off"),
        })
        {
            var item = new ToolStripMenuItem(title) { Checked = WeatherMonitor.Animation == animation };
            item.Click += (_, _) =>
            {
                WeatherMonitor.Animation = animation;
                foreach (ToolStripMenuItem peer in weatherAnimationMenu.DropDownItems) peer.Checked = peer == item;
                DeviceClient.PushWeatherSettings();
            };
            KeepOpenOnClick(item);
            weatherAnimationMenu.DropDownItems.Add(item);
        }
        weatherMenu.DropDownItems.Add(weatherAnimationMenu);
        KeepOpenWhileSetting(weatherMenu.DropDown);
        KeepOpenWhileSetting(weatherAnimationMenu.DropDown);
        contentMenu.DropDownItems.Add(weatherMenu);
        _menu.Items.Add(contentMenu);

        var appearanceMenu = new ToolStripMenuItem("桌宠与外观");
        appearanceMenu.DropDownItems.Add(MakeItem("更换桌宠动画…（petdex）", (_, _) => OpenPetPicker()));
        var resetMenu = new ToolStripMenuItem("恢复默认动画");
        foreach (var (title, slot) in new[] { ("Claude 恢复默认", "claude"), ("Codex 恢复默认", "codex") })
        {
            var item = new ToolStripMenuItem(title);
            item.Click += async (_, _) => await ResetSprite(slot);
            resetMenu.DropDownItems.Add(item);
        }
        appearanceMenu.DropDownItems.Add(resetMenu);
        _menu.Items.Add(appearanceMenu);

        var serviceMenu = new ToolStripMenuItem("桥接服务");
        serviceMenu.DropDownItems.Add(MakeItem("刷新状态", (_, _) =>
        {
            _usage.Refresh();
            _domesticUsage.Refresh(_domesticProvider);
            RefreshUsageLines();
            _ = RefreshDeviceSection();
        }));
        serviceMenu.DropDownItems.Add(MakeItem("桥接服务地址", (_, _) => ShowAddress()));
        serviceMenu.DropDownItems.Add(new ToolStripSeparator());
        _startupItem.Click += (_, _) => ToggleStartup();
        serviceMenu.DropDownItems.Add(_startupItem);
        _menu.Items.Add(serviceMenu);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(MakeItem("退出", (_, _) =>
        {
            _trayIcon.Visible = false;
            Application.Exit();
        }));
    }

    static ToolStripMenuItem MakeItem(string title, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += onClick;
        return item;
    }

    void KeepOpenOnClick(ToolStripMenuItem item)
    {
        item.MouseDown += (_, _) => _keepMenuOpen = true;
        item.Click += (_, _) => BeginInvoke(() => _keepMenuOpen = false);
    }

    void KeepOpenWhileSetting(ToolStripDropDown menu)
    {
        menu.Closing += (_, e) =>
        {
            if (_keepMenuOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                e.Cancel = true;
        };
    }

    void BeginInvoke(Action action)
    {
        if (_menu.IsHandleCreated) _menu.BeginInvoke(action);
        else action();
    }

    void LoadCycleSettings()
    {
        var configured = Settings.Get(CycleEnabledKey);
        _cycleEnabled = configured.Length == 0 || configured == "1";
        if (configured.Length == 0)
        {
            Settings.Set(CycleEnabledKey, "1");
            if (Settings.Get(CyclePagesKey).Length == 0)
                Settings.Set(CyclePagesKey, string.Join(",", DefaultCycleModes));
        }
        _cycleIntervalSeconds = int.TryParse(Settings.Get(CycleIntervalKey), out var seconds)
            && new[] { 10, 15, 30, 60 }.Contains(seconds) ? seconds : 15;
        _cycleTimer.Interval = _cycleIntervalSeconds * 1000;
    }

    void LoadDomesticProviderSettings()
    {
        var configured = Settings.Get(DomesticProviderKey);
        _domesticProvider = DomesticProviderCatalog.All.Any(x => x.Id == configured)
            ? configured : "qwen";
        _service.DomesticProviderOverride = _domesticProvider;
    }

    void UpdateDomesticProviderMenu()
    {
        foreach (var (id, item) in _domesticProviderItems)
            item.Checked = id == _domesticProvider;
    }

    async Task SetDomesticProvider(string provider)
    {
        _domesticProvider = provider;
        _service.DomesticProviderOverride = provider;
        Settings.Set(DomesticProviderKey, provider);
        UpdateDomesticProviderMenu();
        _domesticUsage.Refresh(provider, force: true);
        await SetDisplayMode("domestic");
    }

    void LoadScreenSaverSettings()
    {
        _screenSaverTimeoutMinutes = int.TryParse(Settings.Get(ScreenSaverTimeoutKey), out var minutes)
            && new[] { 0, 1, 5, 10, 30, 60 }.Contains(minutes) ? minutes : 0;
        var previous = Settings.Get(ScreenSaverPreviousModeKey);
        if (previous.Length > 0 && previous != "screensaver") _modeBeforeScreenSaver = previous;
    }

    void UpdateScreenSaverMenu()
    {
        if (_screenSaverMenu == null) return;
        _screenSaverMenu.Text = _screenSaverTimeoutMinutes == 0
            ? "屏保设置：已关闭" : $"屏保设置：{_screenSaverTimeoutMinutes} 分钟";
        foreach (var (minutes, item) in _screenSaverTimeoutItems)
            item.Checked = minutes == _screenSaverTimeoutMinutes;
    }

    async Task SetScreenSaverTimeout(int minutes)
    {
        _screenSaverTimeoutMinutes = minutes;
        Settings.Set(ScreenSaverTimeoutKey, minutes.ToString());
        _lastScreenSaverActivityAt = DateTime.UtcNow;
        UpdateScreenSaverMenu();
        if (minutes == 0 && _screenSaverActive) await ExitScreenSaver();
    }

    bool HasModelActivity()
    {
        var snap = _service.Snapshot();
        return snap.Claude.NeedsInput || snap.Codex.NeedsInput || snap.Domestic.NeedsInput
            || snap.Claude.Status == "working" || snap.Codex.Status == "working"
            || snap.Domestic.Status == "working";
    }

    bool ShouldWakeForMusic() => (_screenSaverActive ? _modeBeforeScreenSaver : _lastKnownMode) == "auto"
        && _nowPlaying.Snapshot.Playing;

    async Task ScreenSaverTick()
    {
        if (_screenSaverBusy) return;
        var now = DateTime.UtcNow;
        var systemIdle = SystemIdleTime.Current;
        var modelActivity = HasModelActivity();
        var musicWake = ShouldWakeForMusic();
        var important = modelActivity || musicWake;
        if (important || systemIdle < TimeSpan.FromSeconds(2)) _lastScreenSaverActivityAt = now;

        if (_screenSaverActive)
        {
            if (_screenSaverPreviewActive)
            {
                if (now < _ignoreScreenSaverWakeUntil) return;
                await ExitScreenSaver();
                return;
            }
            // Model work and approval temporarily override screensaver inside
            // firmware, then return to it if the user is still away. Real user
            // input exits permanently; AUTO music resumes the normal AUTO page.
            if (musicWake || systemIdle < TimeSpan.FromSeconds(2)) await ExitScreenSaver();
            return;
        }
        if (_screenSaverTimeoutMinutes <= 0 || important) return;
        var effectiveIdle = Math.Min(systemIdle.TotalSeconds, (now - _lastScreenSaverActivityAt).TotalSeconds);
        if (effectiveIdle >= _screenSaverTimeoutMinutes * 60) await EnterScreenSaver(preview: false);
    }

    async Task RecoverScreenSaverState()
    {
        try
        {
            var info = await DeviceClient.FetchInfo();
            _lastKnownMode = info.Mode;
            if (info.Mode != "screensaver") return;
            _screenSaverActive = true;
            if (_cycleEnabled) _cycleTimer.Stop();
        }
        catch { }
    }

    async Task EnterScreenSaver(bool preview)
    {
        if (_screenSaverBusy || _screenSaverActive) return;
        _screenSaverBusy = true;
        try
        {
            var info = await DeviceClient.FetchInfo();
            _modeBeforeScreenSaver = info.Mode == "screensaver" ? "auto" : info.Mode;
            Settings.Set(ScreenSaverPreviousModeKey, _modeBeforeScreenSaver);
            _screenSaverActive = true;
            _screenSaverPreviewActive = preview;
            _ignoreScreenSaverWakeUntil = DateTime.MinValue;
            if (_cycleEnabled) _cycleTimer.Stop();
            await DeviceClient.SetDisplayMode(preview ? "screensaver_preview" : "screensaver");
            if (preview) _ignoreScreenSaverWakeUntil = DateTime.UtcNow.AddSeconds(5);
            _lastKnownMode = "screensaver";
            await RefreshDeviceSection();
        }
        catch (Exception e)
        {
            _screenSaverActive = false;
            _screenSaverPreviewActive = false;
            Toast("进入屏保失败", e.Message);
        }
        finally { _screenSaverBusy = false; }
    }

    async Task ExitScreenSaver()
    {
        if (_screenSaverBusy || !_screenSaverActive) return;
        _screenSaverBusy = true;
        var wasPreview = _screenSaverPreviewActive;
        try
        {
            _screenSaverActive = false;
            _screenSaverPreviewActive = false;
            _lastScreenSaverActivityAt = DateTime.UtcNow;
            await DeviceClient.SetDisplayMode(_modeBeforeScreenSaver);
            _lastKnownMode = _modeBeforeScreenSaver;
            if (_cycleEnabled) _cycleTimer.Start();
            await RefreshDeviceSection();
        }
        catch (Exception e)
        {
            _screenSaverActive = true;
            _screenSaverPreviewActive = wasPreview;
            Toast("退出屏保失败", e.Message);
        }
        finally { _screenSaverBusy = false; }
    }

    List<string> CyclePages()
    {
        var valid = CycleModes.Select(x => x.Mode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var saved = Settings.Get(CyclePagesKey)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(valid.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_cyclePageItems.Count != CycleModes.Length) return saved;

        var ordered = saved.Where(mode => _cyclePageItems[mode].Checked).ToList();
        ordered.AddRange(CycleModes.Select(x => x.Mode)
            .Where(mode => _cyclePageItems[mode].Checked && !ordered.Contains(mode)));
        return ordered;
    }

    void SaveCyclePages()
    {
        var pages = CyclePages();
        Settings.Set(CyclePagesKey, string.Join(",", pages));
        _cycleIndex = -1;
        if (_cycleEnabled && pages.Count == 0) _ = SetCycleEnabled(false, restoreAuto: true);
    }

    void EditCycleOrder()
    {
        var pages = CyclePages();
        if (pages.Count < 2)
        {
            Toast("循环展示", "请先在“循环页面”中至少选择两个页面。");
            return;
        }
        var ordered = CycleOrderDialog.Show(pages, CycleModes);
        if (ordered == null) return;
        Settings.Set(CyclePagesKey, string.Join(",", ordered));
        _cycleIndex = -1;
    }

    void EnsureDefaultCyclePages()
    {
        foreach (var mode in DefaultCycleModes)
            _cyclePageItems[mode].Checked = true;
        SaveCyclePages();
    }

    void UpdateCycleMenu()
    {
        _cycleEnabledItem.Checked = _cycleEnabled;
        _cycleEnabledItem.Text = _cycleEnabled
            ? $"循环展示：已开启（每 {_cycleIntervalSeconds} 秒）"
            : "启用循环展示";
        foreach (var (seconds, item) in _cycleIntervalItems) item.Checked = seconds == _cycleIntervalSeconds;
    }

    void SetCycleInterval(int seconds)
    {
        _cycleIntervalSeconds = seconds;
        _cycleTimer.Interval = seconds * 1000;
        Settings.Set(CycleIntervalKey, seconds.ToString());
        UpdateCycleMenu();
    }

    async Task SetCycleEnabled(bool enabled, bool restoreAuto)
    {
        if (enabled && CyclePages().Count == 0) EnsureDefaultCyclePages();
        _cycleEnabled = enabled;
        Settings.Set(CycleEnabledKey, enabled ? "1" : "0");
        UpdateCycleMenu();
        if (enabled)
        {
            _cycleIndex = -1;
            _cycleTimer.Start();
            await AdvanceCycle();
            return;
        }

        _cycleTimer.Stop();
        if (!restoreAuto) return;
        try
        {
            await DeviceClient.SetDisplayMode("auto");
            await RefreshDeviceSection();
        }
        catch (Exception e)
        {
            Toast("停止循环失败", e.Message);
        }
    }

    async Task AdvanceCycle()
    {
        if (!_cycleEnabled || _cycleBusy || _screenSaverActive) return;
        var pages = CyclePages();
        if (pages.Count == 0) return;
        _cycleBusy = true;
        try
        {
            _cycleIndex = (_cycleIndex + 1) % pages.Count;
            await DeviceClient.SetDisplayMode(pages[_cycleIndex]);
            await RefreshDeviceSection();
        }
        catch (Exception e)
        {
            _deviceInfoItem.Text = $"循环展示：切换失败（{e.Message}）";
        }
        finally
        {
            _cycleBusy = false;
        }
    }

    // MARK: - refresh

    void RefreshUsageLines()
    {
        _claudeUsageItem.Text = UsageLine("Claude", _usage.Claude, "7天");
        _codexUsageItem.Text = UsageLine("Codex", _usage.Codex, "周");
    }

    static string UsageLine(string name, ProviderUsage u, string weeklyLabel)
    {
        var heading = string.IsNullOrWhiteSpace(u.Plan) ? name : $"{name} [{u.Plan}]";
        if (u.Error != null && u.PrimaryPct == null && u.WeeklyPct == null)
            return $"{heading}：{u.Error}";
        var parts = new List<string>();
        if (u.PrimaryPct.HasValue)
        {
            var s = $"5h {(int)u.PrimaryPct.Value}%";
            if (u.PrimaryResetMin.HasValue) s += $"（{FmtMin(u.PrimaryResetMin.Value)}后重置）";
            parts.Add(s);
        }
        if (u.WeeklyPct.HasValue)
        {
            var s = $"{weeklyLabel} {(int)u.WeeklyPct.Value}%";
            if (u.WeeklyResetMin.HasValue) s += $"（{FmtMin(u.WeeklyResetMin.Value)}）";
            parts.Add(s);
        }
        return parts.Count == 0 ? $"{heading}：额度未知" : $"{heading}　" + string.Join("　", parts);
    }

    static string FmtMin(int min)
    {
        if (min >= 48 * 60) return $"{min / (24 * 60)}天";
        if (min >= 60) return $"{min / 60}h{(min % 60 > 0 ? $"{min % 60}m" : "")}";
        return $"{min}m";
    }

    async Task RefreshDeviceSection()
    {
        var host = DeviceClient.Host;
        var usb = DeviceClient.Usb;
        if (host.Length == 0 && usb?.Connected != true)
        {
            _deviceInfoItem.Text = "设备：未设置地址";
            foreach (var item in _modeItems.Values) item.Checked = false;
            return;
        }
        _deviceInfoItem.Text = usb?.Connected == true
            ? $"设备：USB {usb.PortName}（连接中…）"
            : $"设备：{host}（连接中…）";
        DeviceInfo info;
        try
        {
            info = await DeviceClient.FetchInfo();
        }
        catch (Exception)
        {
            _deviceInfoItem.Text = usb?.Connected == true
                ? $"设备：USB {usb.PortName}（无法读取）"
                : $"设备：{host}（无法连接）";
            foreach (var item in _modeItems.Values) item.Checked = false;
            // self-heal: the device may have moved to a new DHCP address;
            // if it recently polled us from a different IP, adopt that.
            var seen = DeviceClient.LastSeenIp;
            if (seen.Length > 0 && !host.StartsWith(seen) && await DeviceClient.VerifyDevice(seen))
            {
                DeviceClient.Host = seen;
                await RefreshDeviceSection();
            }
            return;
        }
        var sprites = new[]
        {
            info.ClaudeCustomSprite ? "C:自定义" : "C:默认",
            info.CodexCustomSprite ? "X:自定义" : "X:默认",
        };
        _lastKnownMode = info.Mode;
        var showing = info.Mode == "net" ? "系统监控"
            : info.Mode == "music" ? "音乐"
            : info.Mode == "domestic" ? "国产模型"
            : info.Mode == "dual" ? "Claude + Codex 额度"
            : info.Mode == "stock" ? "股票"
            : info.Mode == "weather" ? "天气"
            : info.Mode == "screensaver" ? "屏保"
            : (info.Showing == "claude" ? "Claude" : "Codex");
        var connection = usb?.Connected == true ? $"USB {usb.PortName}" : info.Ip;
        _deviceInfoItem.Text =
            $"设备：{connection} · 正在显示 {showing} · {string.Join(" ", sprites)}";
        foreach (var (mode, item) in _modeItems) item.Checked = mode == info.Mode;
    }

    // MARK: - pairing

    async Task AutoPairAction()
    {
        _deviceInfoItem.Text = "设备：正在查找…";
        var ip = await DeviceClient.AutoPair(msg => _deviceInfoItem.Text = $"设备：{msg}");
        if (ip != null)
        {
            Toast("配对成功", $"已找到设备并配对：{ip}");
        }
        else
        {
            Toast("未找到设备", """
                局域网内没有发现 ESP8266 时钟。请确认：
                1. 设备已通电并连上同一个 WiFi（首次使用需通过 AI-Clock-Setup 热点配网）
                2. 路由器未开启"客户端隔离"
                """);
        }
        await RefreshDeviceSection();
    }

    // MARK: - actions

    void SetDeviceAddress()
    {
        var input = InputDialog.Show(
            "设备地址",
            "ESP8266 时钟的 IP（设备开机时屏幕上会显示，例如 192.168.1.50）",
            DeviceClient.Host, "192.168.1.50");
        if (input == null) return;
        DeviceClient.Host = input.Trim();
        _ = RefreshDeviceSection();
    }

    void OpenDevicePage()
    {
        var url = DeviceClient.BaseUrl;
        if (url == null)
        {
            SetDeviceAddress();
            return;
        }
        Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
    }

    async Task SetDisplayMode(string mode)
    {
        try
        {
            if (_screenSaverActive)
            {
                _screenSaverActive = false;
                _screenSaverPreviewActive = false;
                _lastScreenSaverActivityAt = DateTime.UtcNow;
            }
            if (_cycleEnabled) await SetCycleEnabled(false, restoreAuto: false);
            await DeviceClient.SetDisplayMode(mode);
            _lastKnownMode = mode;
            await RefreshDeviceSection();
        }
        catch (Exception e)
        {
            Toast("切换失败", e.Message);
        }
    }

    void OpenPetPicker()
    {
        if (DeviceClient.Host.Length == 0) SetDeviceAddress();
        PetPickerForm.ShowShared();
    }

    async Task ResetSprite(string slot)
    {
        try
        {
            await DeviceClient.ResetSprite(slot);
            await RefreshDeviceSection();
        }
        catch (Exception e)
        {
            Toast("恢复失败", e.Message);
        }
    }

    async Task PointBridgeHere()
    {
        var ip = DeviceClient.LocalIPv4();
        if (ip == null)
        {
            Toast("失败", "获取本机局域网 IP 失败");
            return;
        }
        var bridge = $"{ip}:{_port}";
        try
        {
            await DeviceClient.SetBridgeHost(bridge);
            Toast("已设置", $"设备将从 http://{bridge}/status 拉取状态");
        }
        catch (Exception e)
        {
            Toast("设置失败", e.Message);
        }
    }

    void ShowAddress()
    {
        var ip = DeviceClient.LocalIPv4() ?? "<本机局域网IP>";
        Toast("桥接服务地址",
              $"http://{ip}:{_port}/status\n\n设备端 Bridge host 填：{ip}:{_port}");
    }

    void OpenWeatherSettings()
    {
        using var form = new WeatherSettingsForm(_weather);
        form.ShowDialog();
    }

    void OpenStockSettings()
    {
        using var form = new StockSettingsForm(_stocks);
        form.ShowDialog();
    }

    void ToggleStartup()
    {
        try
        {
            var enable = !StartupManager.IsEnabled;
            StartupManager.SetEnabled(enable);
            _startupItem.Checked = enable;
            Toast(enable ? "已开启" : "已关闭",
                  enable ? "AI Clock Bridge 将在登录 Windows 后自动启动。"
                         : "AI Clock Bridge 已取消随 Windows 启动。");
        }
        catch (Exception e)
        {
            Toast("设置失败", e.Message);
        }
    }

    static void Toast(string title, string text)
    {
        MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

static class CycleOrderDialog
{
    public static List<string> Show(
        IReadOnlyList<string> current,
        IReadOnlyList<(string Title, string Mode)> catalog)
    {
        var titles = catalog.ToDictionary(x => x.Mode, x => x.Title);
        var order = current.ToList();
        using var form = new Form
        {
            Text = "调整循环展示顺序",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Microsoft YaHei UI", 9f),
            ClientSize = new Size(390, 330),
            TopMost = true,
        };
        var tip = new Label
        {
            Text = "设备将按从上到下的顺序循环展示。",
            AutoSize = false,
        };
        tip.SetBounds(16, 14, 358, 24);
        var list = new ListBox();
        list.SetBounds(16, 43, 270, 225);
        foreach (var mode in order) list.Items.Add(titles[mode]);
        if (list.Items.Count > 0) list.SelectedIndex = 0;

        var up = new Button { Text = "上移" };
        var down = new Button { Text = "下移" };
        up.SetBounds(300, 72, 74, 30);
        down.SetBounds(300, 112, 74, 30);
        void Move(int delta)
        {
            var from = list.SelectedIndex;
            var to = from + delta;
            if (from < 0 || to < 0 || to >= order.Count) return;
            (order[from], order[to]) = (order[to], order[from]);
            var item = list.Items[from];
            list.Items.RemoveAt(from);
            list.Items.Insert(to, item);
            list.SelectedIndex = to;
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);

        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        ok.SetBounds(204, 286, 80, 30);
        cancel.SetBounds(294, 286, 80, 30);
        form.Controls.AddRange(new Control[] { tip, list, up, down, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? order : null;
    }
}

// Small modal prompt, the NSAlert-with-text-field equivalent.
static class InputDialog
{
    public static string Show(string title, string message, string value, string placeholder)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Microsoft YaHei UI", 9f),
            ClientSize = new Size(380, 140),
            TopMost = true,
        };
        var label = new Label { Text = message };
        label.SetBounds(14, 12, 352, 40);
        var textBox = new TextBox { Text = value, PlaceholderText = placeholder };
        textBox.SetBounds(14, 58, 352, 24);
        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK };
        ok.SetBounds(196, 96, 80, 28);
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(286, 96, 80, 28);
        form.Controls.AddRange(new Control[] { label, textBox, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
    }
}
