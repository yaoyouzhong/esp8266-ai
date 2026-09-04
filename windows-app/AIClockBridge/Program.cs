using System.Text;
using System.Text.Json;
using Microsoft.Win32;
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
        var startupLaunch = args.Length >= 1 && args[0] == "--startup-launch";
        if (startupLaunch) StartupManager.Log($"launch requested pid={Environment.ProcessId}");
        Application.ThreadException += (_, e) => StartupManager.Log($"UI crash: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupManager.Log($"fatal crash: {e.ExceptionObject}");
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
            try
            {
                if (args[1] == "enable") StartupManager.SetEnabled(true);
                else if (args[1] == "disable") StartupManager.SetEnabled(false);
                Console.WriteLine(StartupManager.IsEnabled ? "enabled" : "disabled");
            }
            catch (Exception e)
            {
                StartupManager.Log($"startup registration failed: {e}");
                Environment.ExitCode = 1;
            }
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
        if (args.Length >= 1 && args[0] == "--test-usb-alerts")
        {
            Environment.Exit(TestUsbAlerts().GetAwaiter().GetResult());
            return;
        }
        if (args.Length >= 1 && args[0] == "--test-usb-host-away")
        {
            Environment.Exit(TestUsbHostAway().GetAwaiter().GetResult());
            return;
        }
        if (args.Length >= 1 && args[0] == "--test-quota-window")
        {
            Environment.Exit(TestQuotaWindow());
            return;
        }
        if (args.Length >= 1 && args[0] == "--test-minimax-quota-parser")
        {
            Environment.Exit(TestMiniMaxQuotaParser());
            return;
        }
        if (args.Length >= 1 && args[0] == "--test-codex-reset-credits-parser")
        {
            Environment.Exit(TestCodexResetCreditsParser());
            return;
        }
        if (args.Length >= 1 && args[0] == "--test-webview-recovery")
        {
            Environment.Exit(TestWebViewRecovery());
            return;
        }

        using var singleInstance = new Mutex(true, @"Local\AIClockBridge.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (startupLaunch) StartupManager.Log("startup skipped: another instance is already running");
            return;
        }

        ApplicationConfiguration.Initialize();
        try { StartupManager.EnsureCurrentRegistration(); }
        catch (Exception e) { StartupManager.Log($"startup registration migration failed: {e.Message}"); }

        var service = new StatusService();
        service.DomesticProviderOverride = ConfiguredDomesticProvider();
        service.CodexCompletion = CompletionChime.Play;
        var codexWasForeground = ForegroundApp.IsCodex;
        using var completionAcknowledger = new System.Threading.Timer(_ =>
        {
            var codexIsForeground = ForegroundApp.IsCodex;
            var returnedToCodex = codexIsForeground && !codexWasForeground;
            var clickedCurrentCodex = codexIsForeground && service.CodexCompletionActive
                && SystemIdleTime.Current < TimeSpan.FromMilliseconds(350);
            if (returnedToCodex || clickedCurrentCodex) service.AcknowledgeCodexCompletion();
            codexWasForeground = codexIsForeground;
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
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
        SessionEndingEventHandler sessionEnding = (_, _) => serialBridge.NotifyHostGoingAway();
        SystemEvents.SessionEnding += sessionEnding;
        service.UrgentUpdate = serialBridge.PushStatusNow;
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
        if (startupLaunch) StartupManager.Log($"ready pid={Environment.ProcessId}");
        try { Application.Run(context); }
        finally
        {
            SystemEvents.SessionEnding -= sessionEnding;
            serialBridge.NotifyHostGoingAway();
            StartupManager.Log("bridge stopped");
        }
    }

    static string ConfiguredDomesticProvider()
    {
        var configured = Settings.Get("domestic_provider");
        return DomesticProviderCatalog.All.Any(x => x.Id == configured) ? configured : "";
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

    static int TestQuotaWindow()
    {
        ApplicationConfiguration.Initialize();
        using var form = new DomesticQuotaAuthForm(new DomesticQuotaService(), hideOnUserClose: false);
        using var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        var passed = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            form.ShowAuthorization("qwen");
            passed = form.Visible && form.Opacity == 1 && form.ShowInTaskbar
                && form.WindowState == FormWindowState.Maximized
                && Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(form.Bounds));
            Console.Error.WriteLine(passed
                ? "[test-quota-window] first authorization click restored the window on-screen"
                : $"[test-quota-window] failed: visible={form.Visible}, opacity={form.Opacity}, state={form.WindowState}, bounds={form.Bounds}");
            form.Close();
            Application.ExitThread();
        };
        form.RefreshInBackground("qwen");
        timer.Start();
        Application.Run();
        return passed ? 0 : 1;
    }

    static int TestMiniMaxQuotaParser()
    {
        const string json = """
        {
          "model_remains": [
            {
              "model_name": "video",
              "remains_time": 23965834,
              "weekly_remains_time": 23965834,
              "current_interval_remaining_percent": 100,
              "current_weekly_remaining_percent": 100
            },
            {
              "model_name": "general",
              "remains_time": 14998196,
              "weekly_remains_time": 547798196,
              "current_interval_remaining_percent": 99,
              "current_weekly_remaining_percent": 99
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var before = DateTimeOffset.Now;
        var usage = DomesticQuotaAuthForm.FindMiniMaxUsage(doc.RootElement);
        var after = DateTimeOffset.Now;
        var fiveHourMin = usage?.FiveHourResetAt.HasValue == true
            ? (usage.Value.FiveHourResetAt.Value - before).TotalMinutes : double.NaN;
        var weeklyMin = usage?.WeeklyResetAt.HasValue == true
            ? (usage.Value.WeeklyResetAt.Value - before).TotalMinutes : double.NaN;
        var passed = usage.HasValue
            && Math.Abs(usage.Value.FiveHourPct.GetValueOrDefault() - 1) < 0.001
            && Math.Abs(usage.Value.WeeklyPct - 1) < 0.001
            && fiveHourMin >= 249 && fiveHourMin <= 251
            && weeklyMin >= 9129 && weeklyMin <= 9131
            && usage.Value.FiveHourResetAt >= before && usage.Value.FiveHourResetAt <= after.AddMinutes(251)
            && usage.Value.WeeklyResetAt >= before && usage.Value.WeeklyResetAt <= after.AddMinutes(9131);
        Console.Error.WriteLine(passed
            ? $"[test-minimax-quota-parser] 5H={fiveHourMin:0.0}m, weekly={weeklyMin:0.0}m"
            : $"[test-minimax-quota-parser] failed: 5H={fiveHourMin:0.0}m, weekly={weeklyMin:0.0}m");
        return passed ? 0 : 1;
    }

    static int TestCodexResetCreditsParser()
    {
        var first = DateTimeOffset.UtcNow.AddDays(3);
        var second = DateTimeOffset.UtcNow.AddDays(10);
        var json = $$"""
        {
          "available_count": 2,
          "credits": [
            { "status": "available", "expires_at": "{{second:O}}" },
            { "status": "used", "expires_at": "{{first.AddDays(20):O}}" },
            { "status": "available", "expires_at": "{{first:O}}" }
          ]
        }
        """;
        using var source = JsonDocument.Parse(json);
        var parsed = UsageFetcher.ParseCodexResetCredits(source.RootElement);
        var rows = MirrorControl.ResetCreditRows(parsed.Available, parsed.ExpiresAt,
            parsed.ExpiresAtList);
        var legacyRows = MirrorControl.ResetCreditRows(2, parsed.ExpiresAt, null);
        var snapshot = new StatusSnapshot(new ClaudeStatus(), new CodexStatus
        {
            ResetCreditsAvailable = parsed.Available,
            ResetCreditExpiresAt = parsed.ExpiresAt,
            ResetCreditExpiresAtList = parsed.ExpiresAtList,
        }, new DomesticStatus(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var wire = JsonDocument.Parse(snapshot.ToJson());
        var wireDates = wire.RootElement.GetProperty("codex")
            .GetProperty("reset_credit_expires_at_list");
        var passed = parsed.Available == 2
            && parsed.ExpiresAtList.Length == 2
            && parsed.ExpiresAtList[0] == first.ToUnixTimeSeconds()
            && parsed.ExpiresAtList[1] == second.ToUnixTimeSeconds()
            && rows.Length == 2
            && rows.All(row => row.Count == "R*1" && row.Expiry.Length > 0)
            && legacyRows.Length == 1 && legacyRows[0].Count == "R*2"
            && wireDates.GetArrayLength() == 2;
        Console.Error.WriteLine(passed
            ? "[test-codex-reset-credits-parser] two available credits preserved as two display rows"
            : "[test-codex-reset-credits-parser] failed");
        return passed ? 0 : 1;
    }

    static int TestWebViewRecovery()
    {
        ApplicationConfiguration.Initialize();
        using var form = new DomesticQuotaAuthForm(new DomesticQuotaService(), hideOnUserClose: false);
        Exception uiError = null;
        System.Threading.ThreadExceptionEventHandler exceptionHandler = (_, e) => uiError = e.Exception;
        Application.ThreadException += exceptionHandler;
        var passed = false;
        form.Shown += async (_, _) =>
        {
            try
            {
                var oldPid = await WaitForQuotaBrowser(form, 0, 0, TimeSpan.FromSeconds(20));
                using var browser = System.Diagnostics.Process.GetProcessById((int)oldPid);
                if (!browser.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"refusing to terminate unexpected process {browser.ProcessName} ({oldPid})");
                var recoveryCount = form.WebViewRecoveryCountForTest;
                browser.Kill();
                var newPid = await WaitForQuotaBrowser(
                    form, oldPid, recoveryCount + 1, TimeSpan.FromSeconds(30));
                form.ShowAuthorization("qwen");
                await Task.Delay(1000);
                passed = uiError == null && newPid != oldPid
                    && form.BrowserProcessIdForTest == newPid;
                Console.Error.WriteLine(passed
                    ? $"[test-webview-recovery] browser process {oldPid} -> {newPid}; control recreated and reusable"
                    : $"[test-webview-recovery] failed: old={oldPid}, new={newPid}, current={form.BrowserProcessIdForTest}, ui_error={uiError?.Message}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[test-webview-recovery] failed: {ex}");
            }
            finally
            {
                form.Close();
                Application.ExitThread();
            }
        };
        form.RefreshInBackground("qwen");
        Application.Run();
        Application.ThreadException -= exceptionHandler;
        return passed ? 0 : 1;
    }

    static async Task<uint> WaitForQuotaBrowser(DomesticQuotaAuthForm form, uint excludedPid,
                                                int minimumRecoveryCount, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var pid = form.BrowserProcessIdForTest;
            if (pid != 0 && pid != excludedPid
                && form.WebViewRecoveryCountForTest >= minimumRecoveryCount) return pid;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"WebView2 browser did not become ready within {timeout.TotalSeconds:0} seconds");
    }

    static async Task<int> TestUsb()
    {
        ApplicationConfiguration.Initialize();
        var service = new StatusService();
        var domesticUsage = new DomesticQuotaService();
        service.DomesticUsage = domesticUsage;
        service.DomesticProviderOverride = "kimi";
        var net = new NetSpeedMonitor();
        var music = new NowPlayingMonitor();
        var stocks = new StockMonitor();
        stocks.Start();
        var weather = new WeatherMonitor();
        weather.Start();
        using var usb = new SerialBridge(service, net, music, stocks, weather, telemetryEnabled: true);
        service.UrgentUpdate = usb.PushStatusNow;
        DeviceClient.Usb = usb;
        for (var i = 0; i < 200 && !usb.Connected; i++) await Task.Delay(100);
        if (!usb.Connected)
        {
            Console.Error.WriteLine("[test-usb] device handshake timeout");
            return 1;
        }
        var originalDisplayMode = "auto";
        try
        {
            for (var i = 0; i < 30 && usb.DeviceInfo == null; i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo == null) throw new Exception("device info handshake did not complete");
            originalDisplayMode = string.IsNullOrWhiteSpace(usb.DeviceInfo.Mode)
                ? "auto" : usb.DeviceInfo.Mode;
            usb.SetDisplayMode("codex");
            await Task.Delay(500);
            for (var i = 0; i < 20 && (usb.DeviceInfo?.Effective != "codex"
                     || usb.DeviceInfo?.Showing != "codex"); i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "codex" || usb.DeviceInfo?.Showing != "codex")
                throw new Exception($"Codex single quota page did not activate (effective={usb.DeviceInfo?.Effective ?? "--"}, showing={usb.DeviceInfo?.Showing ?? "--"})");
            var codexQuota = service.Snapshot().Codex;
            Console.Error.WriteLine($"[test-usb] Codex single quota page verified "
                + $"(5H={(codexQuota.PrimaryPct.HasValue ? codexQuota.PrimaryPct.Value.ToString("F0") : "--")}, "
                + $"WK={(codexQuota.WeeklyPct.HasValue ? codexQuota.WeeklyPct.Value.ToString("F0") : "--")})");
            for (var i = 0; i < 150 && (stocks.Snapshot.Length == 0 || weather.Current.UpdatedUtc == 0); i++)
                await Task.Delay(100);
            if (stocks.Snapshot.Length == 0) throw new Exception("stock feed did not load");
            if (weather.Current.UpdatedUtc == 0) throw new Exception("weather feed did not load");

            usb.SetDisplayMode("dual");
            await Task.Delay(500);
            for (var i = 0; i < 20 && usb.DeviceInfo?.Effective != "dual"; i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "dual")
                throw new Exception($"dual quota page did not activate (effective={usb.DeviceInfo?.Effective ?? "--"}, showing={usb.DeviceInfo?.Showing ?? "--"})");
            Console.Error.WriteLine("[test-usb] dual quota page verified");

            usb.SetDisplayMode("domestic");
            await Task.Delay(500);
            for (var i = 0; i < 20 && usb.DeviceInfo?.Effective != "domestic"; i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "domestic")
                throw new Exception("Kimi domestic quota page did not activate");
            var kimiMembership = service.Snapshot().Domestic.Active.Model;
            if (kimiMembership.Length == 0)
                throw new Exception("Kimi membership was not loaded for the domestic quota page");
            if (!service.Snapshot().Domestic.Active.MembershipBadge)
                throw new Exception("Kimi membership badge flag was not set");
            Console.Error.WriteLine($"[test-usb] Kimi domestic quota page verified ({kimiMembership})");
            service.DomesticProviderOverride = "qwen";
            await Task.Delay(1200);
            var qwenMembership = service.Snapshot().Domestic.Active;
            if (qwenMembership.Model != "TEAM" || !qwenMembership.MembershipBadge)
                throw new Exception($"Qwen membership badge was not resolved (model={qwenMembership.Model})");
            Console.Error.WriteLine("[test-usb] Qwen membership badge verified (TEAM)");

            usb.SetDisplayMode("screensaver_preview");
            await Task.Delay(500);
            usb.RequestInfo();
            for (var i = 0; i < 20 && usb.DeviceInfo?.Effective != "screensaver"; i++) await Task.Delay(100);
            if (usb.DeviceInfo?.Effective != "screensaver") throw new Exception("screensaver page did not activate");
            Console.Error.WriteLine("[test-usb] screensaver page verified");

            usb.SetDisplayMode("net");
            await Task.Delay(750);
            usb.RequestInfo();
            for (var i = 0; i < 20 && usb.DeviceInfo?.Effective != "net"; i++) await Task.Delay(100);
            if (usb.DeviceInfo?.Effective != "net") throw new Exception("system monitor page did not activate");
            Console.Error.WriteLine("[test-usb] system monitor immediate data push verified");

            usb.SetDisplayMode("stock");
            for (var i = 0; i < 40 && usb.DeviceInfo?.Effective != "stock"; i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "stock") throw new Exception("stock page did not activate after system monitor");
            service.RecordEvent("codex", "PermissionRequest");
            var approvalSnapshot = service.Snapshot();
            Console.Error.WriteLine($"[test-usb] approval bridge state needs_input={approvalSnapshot.Codex.NeedsInput}, status={approvalSnapshot.Codex.Status}");
            for (var i = 0; i < 40 && (usb.DeviceInfo?.Effective != "auto" || usb.DeviceInfo?.Showing != "codex"); i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "auto" || usb.DeviceInfo?.Showing != "codex")
                throw new Exception($"Codex approval alert did not override pinned page (bridge_needs_input={service.Snapshot().Codex.NeedsInput}, effective={usb.DeviceInfo?.Effective ?? "--"}, showing={usb.DeviceInfo?.Showing ?? "--"})");
            service.RecordEvent("codex", "UserPromptSubmit");
            for (var i = 0; i < 40 && usb.DeviceInfo?.Effective != "stock"; i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "stock") throw new Exception("pinned page did not resume after approval");
            service.RecordEvent("codex", "TaskComplete");
            var completionSnapshot = service.Snapshot();
            Console.Error.WriteLine($"[test-usb] completion bridge state active={completionSnapshot.Codex.CompletionActive}, seq={completionSnapshot.Codex.CompletionSeq}, at={completionSnapshot.Codex.CompletionAt}");
            for (var i = 0; i < 40 && (usb.DeviceInfo?.Effective != "auto" || usb.DeviceInfo?.Showing != "codex"); i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "auto" || usb.DeviceInfo?.Showing != "codex")
                throw new Exception($"Codex completion alert did not activate (bridge_active={service.Snapshot().Codex.CompletionActive}, seq={service.Snapshot().Codex.CompletionSeq}, effective={usb.DeviceInfo?.Effective ?? "--"}, showing={usb.DeviceInfo?.Showing ?? "--"})");
            var firstCompletionSeq = service.Snapshot().Codex.CompletionSeq;
            await Task.Delay(100);
            service.RecordEvent("codex", "TaskComplete");
            if (service.Snapshot().Codex.CompletionSeq <= firstCompletionSeq)
                throw new Exception("second Codex completion was not retained");
            await Task.Delay(7800);
            usb.RequestInfo();
            for (var i = 0; i < 30 && usb.DeviceInfo?.Effective != "stock"; i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "stock") throw new Exception("five-pulse completion alert did not restore pinned page");
            service.AcknowledgeCodexCompletion();
            Console.Error.WriteLine("[test-usb] five-pulse multi-completion alert verified");

            await Task.Delay(3500);
            if (stocks.NameBitmap.Length != StockMonitor.NameW * StockMonitor.NameH * StockMonitor.MaxSymbols * 2)
                throw new Exception("stock names cache does not cover the full watchlist");
            using (var stockJson = JsonDocument.Parse(stocks.ToJson()))
                if (stockJson.RootElement.GetProperty("page_size").GetInt32() != StockMonitor.RowsPerPage)
                    throw new Exception("stock page size is missing from the bridge protocol");
            Console.Error.WriteLine($"[test-usb] stock page {stocks.Snapshot.Length} row(s), names {stocks.NameBitmap.Length} bytes");
            if (stocks.Snapshot.Length > StockMonitor.RowsPerPage)
            {
                usb.SetDisplayMode("stock");
                await Task.Delay(5600);
                usb.RequestInfo();
                for (var i = 0; i < 20 && usb.DeviceInfo?.StockPage != 1; i++)
                {
                    await Task.Delay(100);
                    usb.RequestInfo();
                }
                if (usb.DeviceInfo?.StockPage != 1 || usb.DeviceInfo.StockPageCount < 2)
                    throw new Exception($"stock pagination did not advance (page={usb.DeviceInfo?.StockPage}, count={usb.DeviceInfo?.StockPageCount})");
                Console.Error.WriteLine($"[test-usb] stock pagination verified ({usb.DeviceInfo.StockPage + 1}/{usb.DeviceInfo.StockPageCount})");
            }
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
            usb.ResetSprite("claude");
            return 1;
        }
        finally
        {
            usb.SetDisplayMode(originalDisplayMode);
            await Task.Delay(500);
            usb.RequestInfo();
            for (var i = 0; i < 20 && usb.DeviceInfo?.Mode != originalDisplayMode; i++)
                await Task.Delay(100);
            if (usb.DeviceInfo?.Mode != originalDisplayMode)
                throw new Exception($"display mode restore failed (expected={originalDisplayMode}, actual={usb.DeviceInfo?.Mode ?? "--"})");
            Console.Error.WriteLine($"[test-usb] display mode restored ({originalDisplayMode})");
        }
    }

    static async Task<int> TestUsbHostAway()
    {
        ApplicationConfiguration.Initialize();
        using var usb = new SerialBridge(new StatusService(), new NetSpeedMonitor(),
            new NowPlayingMonitor(), telemetryEnabled: false);
        for (var i = 0; i < 200 && !usb.Connected; i++) await Task.Delay(100);
        if (!usb.Connected)
        {
            Console.Error.WriteLine("[test-usb-host-away] device handshake timeout");
            return 1;
        }
        for (var i = 0; i < 30 && usb.DeviceInfo == null; i++)
        {
            usb.RequestInfo();
            await Task.Delay(100);
        }
        var configured = usb.DeviceInfo?.Mode ?? "";
        usb.NotifyHostGoingAway();
        for (var i = 0; i < 30; i++)
        {
            usb.RequestInfo();
            await Task.Delay(100);
            if (usb.DeviceInfo?.HostOffline == true && usb.DeviceInfo.Effective == "screensaver")
            {
                Console.Error.WriteLine(
                    $"[test-usb-host-away] verified configured={configured}, effective=screensaver, " +
                    $"host_offline=true, time_source={usb.DeviceInfo.TimeSource}, ntp_synced={usb.DeviceInfo.NtpSynced}");
                return 0;
            }
        }
        Console.Error.WriteLine(
            $"[test-usb-host-away] failed effective={usb.DeviceInfo?.Effective ?? "--"}, host_offline={usb.DeviceInfo?.HostOffline}");
        return 1;
    }

    static async Task<int> TestUsbAlerts()
    {
        ApplicationConfiguration.Initialize();
        var service = new StatusService();
        using var usb = new SerialBridge(service, new NetSpeedMonitor(), new NowPlayingMonitor());
        service.UrgentUpdate = usb.PushStatusNow;
        for (var i = 0; i < 200 && !usb.Connected; i++) await Task.Delay(100);
        if (!usb.Connected)
        {
            Console.Error.WriteLine("[test-usb-alerts] device handshake timeout");
            return 1;
        }
        try
        {
            for (var i = 0; i < 30 && usb.DeviceInfo == null; i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo == null) throw new Exception("device info handshake did not complete");
            for (var i = 0; i < 40 && usb.DeviceInfo?.Effective != "stock"; i++)
            {
                if (i % 10 == 0) usb.SetDisplayMode("stock");
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "stock")
                throw new Exception("stock baseline did not activate");

            service.RecordEvent("codex", "PermissionRequest");
            for (var i = 0; i < 40 && (usb.DeviceInfo?.Effective != "auto" || usb.DeviceInfo?.Showing != "codex"); i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "auto" || usb.DeviceInfo?.Showing != "codex")
                throw new Exception("approval alert did not activate");

            service.RecordEvent("codex", "UserPromptSubmit");
            for (var i = 0; i < 40 && usb.DeviceInfo?.Effective != "stock"; i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "stock")
                throw new Exception("approval clear did not restore stock");

            service.RecordEvent("codex", "TaskComplete");
            for (var i = 0; i < 40 && (usb.DeviceInfo?.Effective != "auto" || usb.DeviceInfo?.Showing != "codex"); i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "auto" || usb.DeviceInfo?.Showing != "codex")
                throw new Exception("completion alert did not activate");
            var firstSeq = service.Snapshot().Codex.CompletionSeq;
            service.RecordEvent("codex", "TaskComplete");
            if (service.Snapshot().Codex.CompletionSeq <= firstSeq)
                throw new Exception("second completion event was not retained");

            await Task.Delay(7800);
            for (var i = 0; i < 30 && usb.DeviceInfo?.Effective != "stock"; i++)
            {
                usb.RequestInfo();
                await Task.Delay(100);
            }
            if (usb.DeviceInfo?.Effective != "stock")
                throw new Exception("five pulses did not restore stock");
            service.AcknowledgeCodexCompletion();
            Console.Error.WriteLine("[test-usb-alerts] approval + multi-completion + five-pulse restore verified");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[test-usb-alerts] failed: {error.Message}");
            return 1;
        }
    }
}
