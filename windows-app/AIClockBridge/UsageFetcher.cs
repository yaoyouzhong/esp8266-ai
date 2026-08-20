using System.Diagnostics;
using System.Text.Json;

namespace AIClockBridge;

// Real quota ("额度") for both CLIs, fetched by reusing the OAuth tokens the
// CLIs already store locally, no extra login. On Windows both live in files
// (no Keychain):
//   Claude: %USERPROFILE%\.claude\.credentials.json, then GET
//           https://api.anthropic.com/api/oauth/usage  (5h + 7d windows)
//   Codex:  %USERPROFILE%\.codex\auth.json, then GET
//           https://chatgpt.com/backend-api/wham/usage (5h + weekly windows)
// Tokens never leave this machine except toward their own vendor's API.

class ProviderUsage
{
    public string Plan;            // normalized display label; empty = unknown
    public double? PrimaryPct;     // 5h window used %
    public int? PrimaryResetMin;   // minutes until it resets
    public double? WeeklyPct;      // 7d / weekly window used %
    public int? WeeklyResetMin;
    public int? ResetCreditsAvailable;  // account-owned Codex reset credits
    public long? ResetCreditExpiresAt;   // earliest available credit, Unix seconds
    public string Error;
    public DateTime? FetchedAt;
    public bool RateLimited;
}

sealed class UsageFetcher
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIClockBridge", "usage-cache.json");
    static readonly string CodexAuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
    static readonly JsonSerializerOptions CacheJson = new() { IncludeFields = true };

    readonly object _lock = new();
    ProviderUsage _claude = new();
    ProviderUsage _codex = new();
    System.Windows.Forms.Timer _timer;
    bool _fetching;
    DateTime _nextAllowedFetch = DateTime.MinValue; // throttle + 429 backoff

    static readonly TimeSpan MinFetchInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(300);

    public UsageFetcher()
    {
        (_claude, _codex) = LoadCache();
    }

    public ProviderUsage Claude { get { lock (_lock) return _claude; } }
    public ProviderUsage Codex { get { lock (_lock) return _codex; } }

    /// Raised on the UI thread after either provider updates.
    public Action OnUpdate;

    System.Threading.SynchronizationContext _ui;

    public void StartAutoRefresh(int intervalSeconds = 120)
    {
        _ui = System.Threading.SynchronizationContext.Current;
        Refresh();
        _timer = new System.Windows.Forms.Timer { Interval = intervalSeconds * 1000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public void Refresh()
    {
        lock (_lock)
        {
            if (_fetching || DateTime.UtcNow < _nextAllowedFetch) return;
            _fetching = true;
        }

        Task.Run(async () =>
        {
            var claude = await FetchClaude();
            var codex = await FetchCodex();
            lock (_lock)
            {
                // Keep the last good numbers when a refresh only produced an
                // error (network hiccup / 429) - stale quota beats no quota.
                _claude = Merge(_claude, claude);
                _codex = Merge(_codex, codex);
                if (HasQuota(claude) || HasQuota(codex)) SaveCache(_claude, _codex);
                _fetching = false;
                var backoff = claude.RateLimited ? RateLimitBackoff : MinFetchInterval;
                _nextAllowedFetch = DateTime.UtcNow + backoff;
            }
            if (claude.Error != null) Console.Error.WriteLine($"[usage] claude: {claude.Error}");
            if (codex.Error != null) Console.Error.WriteLine($"[usage] codex: {codex.Error}");
            if (_ui != null) _ui.Post(_ => OnUpdate?.Invoke(), null);
            else OnUpdate?.Invoke();
        });
    }

    static ProviderUsage Merge(ProviderUsage old, ProviderUsage fresh)
    {
        if (string.IsNullOrEmpty(fresh.Plan)) fresh.Plan = old.Plan;
        if (!HasQuota(fresh) && HasQuota(old))
        {
            return new ProviderUsage
            {
                // A credential/JWT plan is only a fallback hint. When the
                // quota request fails, keep the last plan confirmed together
                // with the cached quota instead of mixing fresh unverified
                // identity data with stale verified usage data.
                Plan = string.IsNullOrEmpty(old.Plan) ? fresh.Plan : old.Plan,
                PrimaryPct = old.PrimaryPct,
                PrimaryResetMin = old.PrimaryResetMin,
                WeeklyPct = old.WeeklyPct,
                WeeklyResetMin = old.WeeklyResetMin,
                ResetCreditsAvailable = old.ResetCreditsAvailable,
                ResetCreditExpiresAt = old.ResetCreditExpiresAt,
                FetchedAt = old.FetchedAt,
                Error = fresh.Error,
            };
        }
        if (fresh.ResetCreditsAvailable == old.ResetCreditsAvailable
            && !fresh.ResetCreditExpiresAt.HasValue)
            fresh.ResetCreditExpiresAt = old.ResetCreditExpiresAt;
        return fresh;
    }

    static bool HasQuota(ProviderUsage usage) =>
        usage?.PrimaryPct != null || usage?.WeeklyPct != null;

    sealed class UsageCache
    {
        public ProviderUsage Claude = new();
        public ProviderUsage Codex = new();
    }

    static (ProviderUsage Claude, ProviderUsage Codex) LoadCache()
    {
        try
        {
            var cache = JsonSerializer.Deserialize<UsageCache>(
                File.ReadAllText(CachePath), CacheJson);
            if (cache != null)
                return (RestoreCached(cache.Claude), RestoreCached(cache.Codex));
        }
        catch
        {
            // Missing or damaged cache: the normal online refresh will replace it.
        }
        return (new ProviderUsage(), new ProviderUsage());
    }

    static ProviderUsage RestoreCached(ProviderUsage usage)
    {
        if (usage == null) return new ProviderUsage();
        usage.Error = null;
        usage.RateLimited = false;
        if (usage.FetchedAt.HasValue)
        {
            var ageMin = Math.Max(0, (int)(DateTime.UtcNow - usage.FetchedAt.Value).TotalMinutes);
            usage.PrimaryResetMin = AdjustCachedReset(usage.PrimaryResetMin, ageMin);
            usage.WeeklyResetMin = AdjustCachedReset(usage.WeeklyResetMin, ageMin);
        }
        return usage;
    }

    static int? AdjustCachedReset(int? resetMin, int ageMin)
    {
        if (!resetMin.HasValue) return null;
        return resetMin.Value > ageMin ? resetMin.Value - ageMin : null;
    }

    static void SaveCache(ProviderUsage claude, ProviderUsage codex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath,
                JsonSerializer.Serialize(new UsageCache
                {
                    Claude = CacheCopy(claude),
                    Codex = CacheCopy(codex),
                }, CacheJson));
        }
        catch
        {
            // Cache is best-effort; live usage remains available in memory.
        }
    }

    static ProviderUsage CacheCopy(ProviderUsage usage) => new()
    {
        Plan = usage.Plan,
        PrimaryPct = usage.PrimaryPct,
        PrimaryResetMin = usage.PrimaryResetMin,
        WeeklyPct = usage.WeeklyPct,
        WeeklyResetMin = usage.WeeklyResetMin,
        ResetCreditsAvailable = usage.ResetCreditsAvailable,
        ResetCreditExpiresAt = usage.ResetCreditExpiresAt,
        FetchedAt = usage.FetchedAt,
    };

    // MARK: - Claude (api.anthropic.com/api/oauth/usage)

    static async Task<ProviderUsage> FetchClaude()
    {
        var usage = new ProviderUsage();
        var creds = ClaudeCredentials();
        if (creds == null)
        {
            usage.Error = "未找到 Claude Code 登录凭据（~/.claude/.credentials.json）";
            return usage;
        }
        usage.Plan = creds.Value.Plan;
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.anthropic.com/api/oauth/usage");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {creds.Value.AccessToken}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.0");

        string body;
        int code;
        try
        {
            using var resp = await Http.SendAsync(req);
            code = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            usage.Error = $"Claude 用量请求失败：{ex.Message}";
            return usage;
        }
        if (code != 200)
        {
            usage.RateLimited = code == 429;
            usage.Error = code == 401 ? "Claude 凭据过期，运行 claude 重新登录"
                : code == 429 ? "Claude 用量接口限流，稍后自动重试"
                : $"Claude 用量接口 HTTP {code}";
            return usage;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var now = DateTimeOffset.UtcNow;
            usage.Plan = PlanLabel(StringOrNull(doc.RootElement, "plan_type"))
                ?? PlanLabel(StringOrNull(doc.RootElement, "subscription_type"))
                ?? usage.Plan;
            if (doc.RootElement.TryGetProperty("five_hour", out var fiveHour))
            {
                usage.PrimaryPct = NumberOrNull(fiveHour, "utilization");
                usage.PrimaryResetMin = MinutesUntil(StringOrNull(fiveHour, "resets_at"), now);
            }
            if (doc.RootElement.TryGetProperty("seven_day", out var sevenDay))
            {
                usage.WeeklyPct = NumberOrNull(sevenDay, "utilization");
                usage.WeeklyResetMin = MinutesUntil(StringOrNull(sevenDay, "resets_at"), now);
            }
            usage.FetchedAt = DateTime.UtcNow;
        }
        catch
        {
            usage.Error = "Claude 用量响应解析失败";
        }
        return usage;
    }

    /// Claude Code on Windows stores OAuth credentials as a plain JSON file:
    /// {"claudeAiOauth":{"accessToken":…}}
    static (string AccessToken, string Plan)? ClaudeCredentials()
    {
        var credFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(credFile));
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("accessToken", out var token)
                && token.ValueKind == JsonValueKind.String)
            {
                var t = token.GetString();
                if (string.IsNullOrEmpty(t)) return null;
                var plan = PlanLabel(StringOrNull(oauth, "subscriptionType"))
                    ?? PlanLabel(StringOrNull(oauth, "subscription_type"));
                return (t, plan);
            }
        }
        catch
        {
            // missing / unreadable
        }
        return null;
    }

    // MARK: - Codex (chatgpt.com/backend-api/wham/usage)

    static async Task<ProviderUsage> FetchCodex()
    {
        var usage = new ProviderUsage();
        var creds = CodexCredentials();
        if (creds == null && File.Exists(CodexAuthPath))
        {
            await TryRefreshCodexCredentials();
            creds = CodexCredentials();
        }
        if (creds == null)
        {
            usage.Error = File.Exists(CodexAuthPath)
                ? "Codex CLI 登录已过期，请运行 codex login 重新登录"
                : "未找到 Codex CLI 登录凭据（~/.codex/auth.json）";
            return usage;
        }

        string body = null;
        int code = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            usage.Plan = creds.Value.Plan;
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://chatgpt.com/backend-api/wham/usage");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {creds.Value.AccessToken}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "AIClockBridge");
            if (creds.Value.AccountId != null)
                req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", creds.Value.AccountId);
            try
            {
                using var resp = await Http.SendAsync(req);
                code = (int)resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                usage.Error = $"Codex 用量请求失败：{ex.Message}";
                return usage;
            }
            if (code != 401 && code != 403) break;
            if (attempt != 0 || !await TryRefreshCodexCredentials()) break;
            creds = CodexCredentials();
            if (creds == null) break;
        }
        if (code < 200 || code > 299)
        {
            usage.Error = code == 401 || code == 403
                ? "Codex CLI 登录已过期，请运行 codex login 重新登录"
                : $"Codex 用量接口 HTTP {code}";
            return usage;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            usage.Plan = PlanLabel(StringOrNull(doc.RootElement, "plan_type")) ?? usage.Plan;
            if (!doc.RootElement.TryGetProperty("rate_limit", out var rateLimit))
            {
                usage.Error = "Codex 用量响应解析失败";
                return usage;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (rateLimit.TryGetProperty("primary_window", out var w1))
                AssignCodexWindow(usage, w1, now, false);
            if (rateLimit.TryGetProperty("secondary_window", out var w2))
                AssignCodexWindow(usage, w2, now, true);
            if (doc.RootElement.TryGetProperty("rate_limit_reset_credits", out var resetCredits))
                usage.ResetCreditsAvailable = IntOrNull(resetCredits, "available_count");
            var resetCreditDetails = await FetchCodexResetCredits(
                creds.Value.AccessToken, creds.Value.AccountId);
            if (resetCreditDetails.Available.HasValue)
                usage.ResetCreditsAvailable = resetCreditDetails.Available;
            usage.ResetCreditExpiresAt = resetCreditDetails.ExpiresAt;
            usage.FetchedAt = DateTime.UtcNow;
        }
        catch
        {
            usage.Error = "Codex 用量响应解析失败";
        }
        return usage;
    }

    static async Task<(int? Available, long? ExpiresAt)> FetchCodexResetCredits(
        string accessToken, string accountId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "AIClockBridge");
        if (accountId != null)
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        try
        {
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (null, null);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var available = IntOrNull(doc.RootElement, "available_count");
            long? earliest = null;
            if (doc.RootElement.TryGetProperty("credits", out var credits)
                && credits.ValueKind == JsonValueKind.Array)
            {
                foreach (var credit in credits.EnumerateArray())
                {
                    if (!string.Equals(StringOrNull(credit, "status"), "available",
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    var expires = StringOrNull(credit, "expires_at");
                    if (!DateTimeOffset.TryParse(expires, out var parsed)) continue;
                    var epoch = parsed.ToUnixTimeSeconds();
                    if (!earliest.HasValue || epoch < earliest.Value) earliest = epoch;
                }
            }
            return (available, earliest);
        }
        catch
        {
            return (null, null);
        }
    }

    static void AssignCodexWindow(ProviderUsage usage, JsonElement window,
                                  double now, bool weeklyFallback)
    {
        if (window.ValueKind != JsonValueKind.Object) return;
        var seconds = NumberOrNull(window, "limit_window_seconds");
        // OpenAI may place the only active window in primary_window. Identify
        // it by its duration: 5h = 18,000s, weekly = 604,800s.
        var weekly = seconds.HasValue ? seconds.Value >= 2 * 24 * 60 * 60 : weeklyFallback;
        var pct = NumberOrNull(window, "used_percent");
        var reset = NumberOrNull(window, "reset_at");
        var resetMin = reset.HasValue ? Math.Max(0, (int)((reset.Value - now) / 60)) : (int?)null;
        if (weekly)
        {
            usage.WeeklyPct = pct;
            usage.WeeklyResetMin = resetMin;
        }
        else
        {
            usage.PrimaryPct = pct;
            usage.PrimaryResetMin = resetMin;
        }
    }

    static (string AccessToken, string AccountId, string Plan)? CodexCredentials()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CodexAuthPath));
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens)
                || !tokens.TryGetProperty("access_token", out var accessEl)
                || accessEl.ValueKind != JsonValueKind.String) return null;
            var access = accessEl.GetString();
            if (string.IsNullOrEmpty(access) || !JwtUsable(access)) return null;
            string accountId = null;
            string plan = null;
            if (tokens.TryGetProperty("account_id", out var acc) && acc.ValueKind == JsonValueKind.String)
                accountId = acc.GetString();
            if (tokens.TryGetProperty("id_token", out var idTok)
                && idTok.ValueKind == JsonValueKind.String)
            {
                accountId ??= AccountIdFromJwt(idTok.GetString());
                plan = PlanFromJwt(idTok.GetString());
            }
            return (access, accountId, plan);
        }
        catch
        {
            return null;
        }
    }

    /// Codex Desktop uses externally managed, memory-only credentials. The
    /// standalone CLI has its own managed auth.json. Ask the official CLI app
    /// server to refresh that file instead of implementing OAuth refresh here.
    static async Task<bool> TryRefreshCodexCredentials()
    {
        var executable = FindCodexExecutable();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--listen");
        process.StartInfo.ArgumentList.Add("stdio://");
        try
        {
            if (!process.Start()) return false;
            _ = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await WriteRpc(process, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "ai-clock-bridge", version = "1.0" },
                },
            }, timeout.Token);
            if (!await ReadRpcSuccess(process, 1, timeout.Token)) return false;
            await WriteRpc(process, new { method = "initialized", @params = new { } }, timeout.Token);
            await WriteRpc(process, new
            {
                id = 2,
                method = "account/read",
                @params = new { refreshToken = true },
            }, timeout.Token);
            return await ReadRpcSuccess(process, 2, timeout.Token);
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The short-lived helper may already have exited.
            }
        }
    }

    static async Task WriteRpc(Process process, object message, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync(token);
    }

    static async Task<bool> ReadRpcSuccess(Process process, int id, CancellationToken token)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line == null) return false;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("id", out var responseId)
                    || !responseId.TryGetInt32(out var value) || value != id)
                    continue;
                return doc.RootElement.TryGetProperty("result", out _)
                    && !doc.RootElement.TryGetProperty("error", out _);
            }
            catch (JsonException)
            {
                // Ignore non-protocol output and continue until the timeout.
            }
        }
    }

    static string FindCodexExecutable()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin", "codex.exe");
        return File.Exists(local) ? local : "codex.exe";
    }

    static bool JwtUsable(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return false;
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0) b64 += "=";
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            if (!doc.RootElement.TryGetProperty("exp", out var exp)
                || !exp.TryGetInt64(out var expiresAt)) return false;
            return DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() < expiresAt;
        }
        catch
        {
            return false;
        }
    }

    /// auth.json without a top-level account_id keeps it inside the id_token
    /// JWT claims (https://api.openai.com/auth -> chatgpt_account_id).
    static string AccountIdFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0) b64 += "=";
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            if (doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth)
                && auth.TryGetProperty("chatgpt_account_id", out var id)
                && id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        catch
        {
            // malformed JWT
        }
        return null;
    }

    static string PlanFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0) b64 += "=";
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            if (doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth))
                return PlanLabel(StringOrNull(auth, "chatgpt_plan_type"));
        }
        catch
        {
            // malformed JWT
        }
        return null;
    }

    // MARK: - helpers

    static double? NumberOrNull(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return null;
    }

    static string StringOrNull(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    static int? IntOrNull(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var value))
            return value;
        return null;
    }

    /// Only known, explicit vendor values are displayed. Unknown values stay
    /// hidden instead of being guessed from usage limits.
    static string PlanLabel(string value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "free" => "FREE",
            "plus" => "PLUS",
            "pro" => "PRO",
            "prolite" or "pro_lite" or "pro-lite" => "PRO LITE",
            "team" => "TEAM",
            "business" => "BUSINESS",
            "enterprise" => "ENTERPRISE",
            "edu" => "EDU",
            "max" => "MAX",
            "max_5x" or "max-5x" or "max5x" => "MAX 5X",
            "max_20x" or "max-20x" or "max20x" => "MAX 20X",
            "claude_pro" or "claude-pro" => "PRO",
            "claude_max" or "claude-max" => "MAX",
            "claude_max_5x" or "claude-max-5x" => "MAX 5X",
            "claude_max_20x" or "claude-max-20x" => "MAX 20X",
            _ => null,
        };
    }

    static int? MinutesUntil(string iso, DateTimeOffset now)
    {
        if (iso == null) return null;
        if (!DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            return null;
        return Math.Max(0, (int)((d - now).TotalMinutes));
    }
}
