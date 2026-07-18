using System.Text;
using System.Text.Json;
using System.Globalization;

namespace AIClockBridge;

// Port of the Mac StatusReader. No account APIs / keys are touched -
// everything comes from the JSONL session logs Claude Code and Codex CLI
// already write to disk (same paths on Windows, under %USERPROFILE%):
//   ~/.claude/projects/**/*.jsonl   (Claude Code transcripts)
//   ~/.codex/sessions/**/*.jsonl    (Codex CLI rollouts, incl. rate_limits)

class ClaudeStatus
{
    public string Plan = "";
    public string Status = "offline";
    public int TokensToday;
    public int SessionMin = 0;
    public int SessionWindowMin = 300;
    public double? FiveHourPct;
    public int? FiveHourResetMin;
    public double? SevenDayPct;
    public int? SevenDayResetMin;
    public bool NeedsInput; // waiting on a permission/approval prompt
}

class CodexStatus
{
    public string Plan = "";
    public string Status = "offline";
    public int TokensToday;
    public double? PrimaryPct;
    public int? PrimaryWindowMin;
    public int? PrimaryResetMin;
    public double? WeeklyPct;
    public int? WeeklyWindowMin;
    public int? WeeklyResetMin;
    public bool NeedsInput;
    public long CompletionAt; // Unix seconds of the latest explicit Stop event
    public long CompletionSeq;
    public bool CompletionActive;
}

class DomesticProviderStatus
{
    public string Model = "";
    public bool MembershipBadge;
    public long TokensToday;
    public double? PlanPct = null;
    public string PlanPctText = "";
    public string RemainingPctText = "";
    public double? FiveHourPct = null;
    public double? WeeklyPct = null;
    public double LastActivityEpoch;
}

class DomesticStatus
{
    public string Status = "offline";
    public string ActiveProvider = "";
    public bool NeedsInput;
    // Generic display payload. New domestic providers populate this field and
    // ActiveProvider; the firmware never needs a provider-specific layout.
    public DomesticProviderStatus Active = new();
    public DomesticProviderStatus Qwen = new();
    public DomesticProviderStatus Xiaomi = new();
    public DomesticProviderStatus Kimi = new();
}

class StatusSnapshot
{
    public ClaudeStatus Claude = new();
    public CodexStatus Codex = new();
    public DomesticStatus Domestic = new();
    public long Ts;
    public bool MusicPlaying;

    /// Serializes to the exact JSON shape the firmware's parseStatusJson expects.
    public byte[] ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("ts", Ts);
            w.WriteNumber("local_utc_offset_s", (int)TimeZoneInfo.Local
                .GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(Ts)).TotalSeconds);
            w.WriteBoolean("music_playing", MusicPlaying);
            w.WriteStartObject("claude");
            w.WriteString("plan", Claude.Plan);
            w.WriteString("status", Claude.Status);
            w.WriteNumber("tokens_today", Claude.TokensToday);
            w.WriteNumber("session_min", Claude.SessionMin);
            w.WriteNumber("session_window_min", Claude.SessionWindowMin);
            WriteNullable(w, "five_hour_pct", Claude.FiveHourPct);
            WriteNullable(w, "five_hour_reset_min", Claude.FiveHourResetMin);
            WriteNullable(w, "seven_day_pct", Claude.SevenDayPct);
            WriteNullable(w, "seven_day_reset_min", Claude.SevenDayResetMin);
            w.WriteBoolean("needs_input", Claude.NeedsInput);
            w.WriteEndObject();
            w.WriteStartObject("codex");
            w.WriteString("plan", Codex.Plan);
            w.WriteString("status", Codex.Status);
            w.WriteNumber("tokens_today", Codex.TokensToday);
            WriteNullable(w, "primary_pct", Codex.PrimaryPct);
            WriteNullable(w, "primary_window_min", Codex.PrimaryWindowMin);
            WriteNullable(w, "primary_reset_min", Codex.PrimaryResetMin);
            WriteNullable(w, "weekly_pct", Codex.WeeklyPct);
            WriteNullable(w, "weekly_window_min", Codex.WeeklyWindowMin);
            WriteNullable(w, "weekly_reset_min", Codex.WeeklyResetMin);
            w.WriteBoolean("needs_input", Codex.NeedsInput);
            w.WriteNumber("completion_at", Codex.CompletionAt);
            w.WriteNumber("completion_seq", Codex.CompletionSeq);
            w.WriteBoolean("completion_active", Codex.CompletionActive);
            w.WriteEndObject();
            w.WriteStartObject("domestic");
            w.WriteString("status", Domestic.Status);
            w.WriteString("active_provider", Domestic.ActiveProvider);
            w.WriteBoolean("needs_input", Domestic.NeedsInput);
            WriteDomesticProvider(w, "active", ActiveDomesticProvider(Domestic));
            WriteDomesticProvider(w, "qwen", Domestic.Qwen);
            WriteDomesticProvider(w, "xiaomi", Domestic.Xiaomi);
            WriteDomesticProvider(w, "kimi", Domestic.Kimi);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    static void WriteNullable(Utf8JsonWriter w, string name, double? v)
    {
        if (v.HasValue) w.WriteNumber(name, v.Value); else w.WriteNull(name);
    }

    static void WriteNullable(Utf8JsonWriter w, string name, int? v)
    {
        if (v.HasValue) w.WriteNumber(name, v.Value); else w.WriteNull(name);
    }

    static void WriteDomesticProvider(Utf8JsonWriter w, string name, DomesticProviderStatus p)
    {
        w.WriteStartObject(name);
        w.WriteString("model", p.Model);
        w.WriteBoolean("membership_badge", p.MembershipBadge);
        w.WriteNumber("tokens_today", p.TokensToday);
        WriteNullable(w, "plan_pct", p.PlanPct);
        w.WriteString("plan_pct_text", p.PlanPctText);
        w.WriteString("remaining_pct_text", p.RemainingPctText);
        WriteNullable(w, "five_hour_pct", p.FiveHourPct);
        WriteNullable(w, "weekly_pct", p.WeeklyPct);
        w.WriteEndObject();
    }

    static DomesticProviderStatus ActiveDomesticProvider(DomesticStatus domestic)
    {
        if (domestic.Active.Model.Length > 0 || domestic.Active.TokensToday > 0
            || domestic.Active.PlanPct.HasValue || domestic.Active.FiveHourPct.HasValue
            || domestic.Active.WeeklyPct.HasValue)
            return domestic.Active;
        return domestic.ActiveProvider switch
        {
            "xiaomi" => domestic.Xiaomi,
            "kimi" => domestic.Kimi,
            _ => domestic.Qwen,
        };
    }

    StatusSnapshot() { }

    public StatusSnapshot(ClaudeStatus claude, CodexStatus codex, DomesticStatus domestic, long ts)
    {
        Claude = claude;
        Codex = codex;
        Domestic = domestic;
        Ts = ts;
    }

    public StatusSnapshot Clone()
    {
        return new StatusSnapshot
        {
            Claude = (ClaudeStatus)Claude.MemberwiseCloneOf(),
            Codex = (CodexStatus)Codex.MemberwiseCloneOf(),
            Domestic = new DomesticStatus
            {
                Status = Domestic.Status,
                ActiveProvider = Domestic.ActiveProvider,
                NeedsInput = Domestic.NeedsInput,
                Active = (DomesticProviderStatus)Domestic.Active.MemberwiseCloneOf(),
                Qwen = (DomesticProviderStatus)Domestic.Qwen.MemberwiseCloneOf(),
                Xiaomi = (DomesticProviderStatus)Domestic.Xiaomi.MemberwiseCloneOf(),
                Kimi = (DomesticProviderStatus)Domestic.Kimi.MemberwiseCloneOf(),
            },
            Ts = Ts,
            MusicPlaying = MusicPlaying,
        };
    }
}

static class CloneHelper
{
    public static object MemberwiseCloneOf(this object o)
    {
        var clone = Activator.CreateInstance(o.GetType());
        foreach (var f in o.GetType().GetFields())
            f.SetValue(clone, f.GetValue(o));
        return clone;
    }
}

/// Reads the logs and derives status, with a small time cache so back-to-back
/// HTTP polls and the mirror timer don't each re-scan the whole tree.
sealed class StatusService
{
    readonly string _claudeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
    readonly string _codexDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
    readonly double _startedAt = Now();

    /// Real OAuth quota (5h/weekly windows) merged into snapshots when set;
    /// log-derived values remain the fallback for offline use.
    public UsageFetcher Usage;
    public DomesticQuotaService DomesticUsage;
    public string DomesticProviderOverride = "qwen";
    public Action CodexCompletion;
    public Action UrgentUpdate;

    /// Whether audio is playing right now (drives the device's AUTO -> music
    /// auto-switch). Set from NowPlayingMonitor in Program.
    public Func<bool> MusicPlayingProvider;

    // Hook-pushed live state (POST /event from Claude Code / Codex hooks).
    // Events beat the mtime heuristic while fresh: "working" for up to 10min
    // (a long tool run emits nothing between PreToolUse and PostToolUse),
    // "idle" for 60s (long enough to kill the mtime tail after Stop, short
    // enough that a session without hooks isn't stuck idle).
    record AgentEvent(string State, double At);

    AgentEvent _claudeEvent;
    AgentEvent _codexEvent;
    // "needs input": a permission/approval prompt is on screen, waiting on the
    // user. Set by an attention event, cleared by the next concrete lifecycle
    // event (the prompt got answered) or by TTL.
    double? _claudeNeedsInputAt;
    double? _codexNeedsInputAt;
    long _codexCompletionAt;
    double _codexCompletionObservedAt;
    long _codexCompletionSeq;
    bool _codexCompletionActive;
    const double WorkingEventTTL = 10 * 60;
    const double IdleEventTTL = 60;
    const double NeedsInputTTL = 5 * 60;

    static readonly HashSet<string> WorkingEvents = new()
    {
        "UserPromptSubmit", "PreToolUse", "PostToolUse", "SubagentStart", "SubagentStop",
        "PreCompact", "PostCompact", "WorktreeCreate",
    };
    static readonly HashSet<string> IdleEvents = new() { "Stop", "SessionEnd", "SessionStart" };
    // Codex PermissionRequest and MCP Elicitation are always a real "act now"
    // prompt. Claude's Notification is broader — it also fires on task
    // completion / 60s-idle — so it only counts as needs-input when its
    // message is actually a permission request.
    static readonly HashSet<string> AttentionEvents = new() { "Elicitation", "PermissionRequest" };

    static bool IsPermissionNotification(string message)
    {
        var m = message?.ToLowerInvariant() ?? "";
        return m.Contains("permission") || m.Contains("approve") || m.Contains("approval");
    }

    /// Called by the /event endpoint. Unknown event names are ignored.
    /// `message` is only sent for Claude's Notification hook.
    public void RecordEvent(string agent, string ev, string message = null)
    {
        var changed = false;
        lock (_lock)
        {
            var now = Now();
            if (agent == "codex" && ev == "Stop")
                changed |= SetCodexCompletion(now);
            // Claude Notification: flash only for permission prompts, not for
            // "task done / waiting for your input" notifications.
            if (ev == "Notification")
            {
                if (IsPermissionNotification(message))
                {
                    if (agent == "claude") _claudeNeedsInputAt = now;
                    else if (agent == "codex") _codexNeedsInputAt = now;
                    changed = agent is "claude" or "codex";
                }
            }
            else if (AttentionEvents.Contains(ev))
            {
                if (agent == "claude") _claudeNeedsInputAt = now;
                else if (agent == "codex") _codexNeedsInputAt = now;
                changed = agent is "claude" or "codex";
            }
            else
            {
                string state = null;
                if (WorkingEvents.Contains(ev)) state = "working";
                else if (IdleEvents.Contains(ev)) state = "idle";
                if (state != null)
                {
                    var e = new AgentEvent(state, now);
                    // any concrete lifecycle event means the prompt (if any) was answered
                    if (agent == "claude")
                    {
                        _claudeEvent = e;
                        _claudeNeedsInputAt = null;
                        changed = true;
                    }
                    else if (agent == "codex")
                    {
                        _codexEvent = e;
                        _codexNeedsInputAt = null;
                        if (state == "working") _codexCompletionActive = false;
                        changed = true;
                    }
                }
            }
        }
        if (changed) QueueCallback(UrgentUpdate);
    }

    static bool NeedsInput(double? at, double now) => at.HasValue && now - at.Value < NeedsInputTTL;

    /// Event override, applied on top of the log-derived status. "offline"
    /// from logs is only upgraded by a fresh working event (a live hook means
    /// the CLI is definitely running).
    static string OverrideStatus(string logStatus, AgentEvent ev, double now)
    {
        if (ev == null) return logStatus;
        var age = now - ev.At;
        if (ev.State == "working" && age < WorkingEventTTL) return "working";
        if (ev.State == "idle" && age < IdleEventTTL && logStatus == "working") return "idle";
        return logStatus;
    }

    const double WorkingThreshold = 20;        // log touched within this -> "working"
    const double IdleThreshold = 30 * 60;      // within this -> "idle", else "offline"
    const double CacheTTL = 5;

    readonly object _lock = new();
    StatusSnapshot _cached;
    double _cachedAt;

    sealed record ClaudeFileSummary(
        long ClaudeTokens, double ClaudeActivity,
        long QwenTokens, string QwenModel, double QwenActivity,
        long XiaomiTokens, string XiaomiModel, double XiaomiActivity);

    // Claude Code JSONL files are append-only. Reuse the parsed aggregate until
    // the file mtime changes instead of rereading the full history every 5s.
    readonly Dictionary<string, (double Mtime, ClaudeFileSummary Summary)> _claudeFileCache = new();

    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    static void SetPercentDisplayText(DomesticProviderStatus provider)
    {
        if (!provider.PlanPct.HasValue)
        {
            provider.PlanPctText = "";
            provider.RemainingPctText = "";
            return;
        }
        provider.PlanPctText = ((int)Math.Clamp(provider.PlanPct.Value, 0, 100))
            .ToString(CultureInfo.InvariantCulture);
        var remaining = Math.Truncate(Math.Max(0, 100 - provider.PlanPct.Value) * 100) / 100;
        provider.RemainingPctText = remaining.ToString("0.00", CultureInfo.InvariantCulture);
    }

    static string QwenMembershipLabel(string membership)
    {
        if (membership.Contains("团队", StringComparison.OrdinalIgnoreCase)
            || membership.Contains("team", StringComparison.OrdinalIgnoreCase)) return "TEAM";
        if (membership.Contains("企业", StringComparison.OrdinalIgnoreCase)
            || membership.Contains("enterprise", StringComparison.OrdinalIgnoreCase)) return "ENTERPRISE";
        if (membership.Contains("个人", StringComparison.OrdinalIgnoreCase)
            || membership.Contains("personal", StringComparison.OrdinalIgnoreCase)
            || membership.Contains("individual", StringComparison.OrdinalIgnoreCase)) return "PERSONAL";
        if (membership.Contains("专业", StringComparison.OrdinalIgnoreCase)
            || membership.Contains("professional", StringComparison.OrdinalIgnoreCase)) return "PRO";
        if (membership.Contains("标准", StringComparison.OrdinalIgnoreCase)
            || membership.Contains("standard", StringComparison.OrdinalIgnoreCase)) return "STANDARD";
        if (membership.Contains("基础", StringComparison.OrdinalIgnoreCase)
            || membership.Contains("basic", StringComparison.OrdinalIgnoreCase)) return "BASIC";
        return "TOKEN PLAN";
    }

    bool SetCodexCompletion(double completionAt)
    {
        if (completionAt <= _codexCompletionObservedAt) return false;
        _codexCompletionObservedAt = completionAt;
        _codexCompletionAt = (long)completionAt;
        _codexCompletionSeq++;
        _codexCompletionActive = true;
        QueueCallback(CodexCompletion);
        return true;
    }

    static void QueueCallback(Action callback)
    {
        if (callback == null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { callback(); } catch { }
        });
    }

    public void AcknowledgeCodexCompletion()
    {
        var changed = false;
        lock (_lock)
        {
            changed = _codexCompletionActive;
            _codexCompletionActive = false;
        }
        if (changed) QueueCallback(UrgentUpdate);
    }

    public bool CodexCompletionActive
    {
        get { lock (_lock) return _codexCompletionActive; }
    }

    public StatusSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = Now();
            StatusSnapshot snap;
            if (_cached != null && now - _cachedAt < CacheTTL)
            {
                snap = _cached.Clone();
            }
            else
            {
                var claude = ReadClaude(out var domestic);
                snap = new StatusSnapshot(claude, ReadCodex(), domestic, (long)now);
                _cached = snap.Clone();
                _cachedAt = now;
            }
            snap.Ts = (long)now;

            // overlays are cheap and applied on every call, so hook events and
            // fresh quota show through instantly even while the log scan is cached
            if (Usage != null)
            {
                var cu = Usage.Claude;
                snap.Claude.Plan = cu.Plan ?? "";
                snap.Claude.FiveHourPct = cu.PrimaryPct;
                snap.Claude.FiveHourResetMin = cu.PrimaryResetMin;
                snap.Claude.SevenDayPct = cu.WeeklyPct;
                snap.Claude.SevenDayResetMin = cu.WeeklyResetMin;
                var xu = Usage.Codex;
                snap.Codex.Plan = xu.Plan ?? "";
                if (xu.PrimaryPct.HasValue)
                {
                    snap.Codex.PrimaryPct = xu.PrimaryPct;
                    snap.Codex.PrimaryResetMin = xu.PrimaryResetMin;
                }
                if (xu.WeeklyPct.HasValue)
                {
                    snap.Codex.WeeklyPct = xu.WeeklyPct;
                    snap.Codex.WeeklyResetMin = xu.WeeklyResetMin;
                }
            }
            if (DomesticUsage != null)
            {
                var du = DomesticUsage.Snapshot;
                snap.Domestic.Qwen.PlanPct = du.QwenPlanPct;
                snap.Domestic.Xiaomi.PlanPct = du.XiaomiPlanPct;
                snap.Domestic.Kimi.PlanPct = du.KimiWeeklyPct;
                snap.Domestic.Kimi.WeeklyPct = du.KimiWeeklyPct;
                snap.Domestic.Kimi.FiveHourPct = du.KimiFiveHourPct;
                if (!string.IsNullOrWhiteSpace(du.QwenMembership))
                {
                    snap.Domestic.Qwen.Model = QwenMembershipLabel(du.QwenMembership);
                    snap.Domestic.Qwen.MembershipBadge = true;
                }
                if (!string.IsNullOrWhiteSpace(du.KimiMembership))
                {
                    snap.Domestic.Kimi.Model = du.KimiMembership;
                    snap.Domestic.Kimi.MembershipBadge = true;
                }
                SetPercentDisplayText(snap.Domestic.Qwen);
                SetPercentDisplayText(snap.Domestic.Xiaomi);
                SetPercentDisplayText(snap.Domestic.Kimi);
                if (DomesticProviderOverride.Length > 0)
                {
                    snap.Domestic.ActiveProvider = DomesticProviderOverride;
                    var selected = DomesticProviderOverride switch
                    {
                        "xiaomi" => snap.Domestic.Xiaomi,
                        "kimi" => snap.Domestic.Kimi,
                        "qwen" => snap.Domestic.Qwen,
                        _ => new DomesticProviderStatus(),
                    };
                    snap.Domestic.Active = (DomesticProviderStatus)selected.MemberwiseCloneOf();
                }
            }
            var domesticIsCurrent = snap.Domestic.ActiveProvider.Length > 0
                && Math.Max(snap.Domestic.Qwen.LastActivityEpoch,
                            snap.Domestic.Xiaomi.LastActivityEpoch) > now - IdleThreshold;
            if (domesticIsCurrent)
            {
                snap.Domestic.Status = OverrideStatus(snap.Domestic.Status, _claudeEvent, now);
                snap.Domestic.NeedsInput = NeedsInput(_claudeNeedsInputAt, now);
            }
            else
            {
                snap.Claude.Status = OverrideStatus(snap.Claude.Status, _claudeEvent, now);
                snap.Claude.NeedsInput = NeedsInput(_claudeNeedsInputAt, now);
            }
            snap.Codex.Status = OverrideStatus(snap.Codex.Status, _codexEvent, now);
            snap.Codex.NeedsInput = NeedsInput(_codexNeedsInputAt, now);
            snap.Codex.CompletionAt = _codexCompletionAt;
            snap.Codex.CompletionSeq = _codexCompletionSeq;
            snap.Codex.CompletionActive = _codexCompletionActive;
            snap.MusicPlaying = MusicPlayingProvider?.Invoke() ?? false;
            return snap;
        }
    }

    // MARK: - helpers

    static string StatusFromDelta(double delta)
    {
        if (delta < WorkingThreshold) return "working";
        if (delta < IdleThreshold) return "idle";
        return "offline";
    }

    static double? ParseIso(string s)
    {
        if (s == null) return null;
        if (DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            return d.ToUnixTimeMilliseconds() / 1000.0;
        return null;
    }

    static double TodayStartEpoch() =>
        new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds() / 1000.0;

    /// Lossy UTF-8 read split into lines (skips files locked by the CLIs).
    static string[] ReadLines(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return null;
        }
    }

    static int IntVal(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number)
            return (int)v.GetDouble();
        return 0;
    }

    static double? DoubleVal(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return null;
    }

    static string StringVal(JsonElement obj, string key)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    static bool TryProp(JsonElement obj, string key, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value);
    }

    // MARK: - Claude

    static string ModelProvider(string model)
    {
        var m = model?.Trim().ToLowerInvariant() ?? "";
        if (m.StartsWith("claude-") || m.Contains("/claude-")) return "claude";
        if (m.StartsWith("qwen") || m.Contains("/qwen")) return "qwen";
        if (m.StartsWith("mimo") || m.Contains("/mimo") || m.Contains("xiaomi")) return "xiaomi";
        return "";
    }

    static double LatestCodexTaskComplete(string path, double afterEpoch)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            const int tailBytes = 128 * 1024;
            var tailStart = Math.Max(0, fs.Length - tailBytes);
            fs.Seek(tailStart, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            if (tailStart > 0) reader.ReadLine(); // discard a partial UTF-8/JSON line
            double latest = 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.Contains("\"task_complete\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!TryProp(root, "payload", out var payload)
                        || StringVal(payload, "type") != "task_complete") continue;
                    var timestamp = ParseIso(StringVal(root, "timestamp")) ?? 0;
                    if (timestamp >= afterEpoch && timestamp > latest) latest = timestamp;
                }
                catch
                {
                    // The final line may still be in flight; the next scan retries it.
                }
            }
            return latest;
        }
        catch
        {
            return 0;
        }
    }

    ClaudeStatus ReadClaude(out DomesticStatus domestic)
    {
        var todayStart = TodayStartEpoch();
        var now = Now();
        var claude = new ClaudeStatus();
        domestic = new DomesticStatus();
        double lastClaudeActivity = 0;

        if (Directory.Exists(_claudeDir))
        {
            var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(_claudeDir, "*.jsonl", SearchOption.AllDirectories);
            }
            catch
            {
                files = Array.Empty<string>();
            }
            foreach (var file in files)
            {
                double mtime;
                try
                {
                    mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)
                        .ToUnixTimeMilliseconds() / 1000.0;
                }
                catch
                {
                    continue;
                }
                if (mtime < todayStart) continue; // no activity today, skip parsing
                livePaths.Add(file);
                if (!_claudeFileCache.TryGetValue(file, out var cached) || cached.Mtime != mtime)
                {
                    var lines = ReadLines(file);
                    if (lines == null) continue; // transient lock: retry next scan
                    cached = (mtime, ParseClaudeFile(lines, todayStart, mtime));
                    _claudeFileCache[file] = cached;
                }
                var summary = cached.Summary;
                claude.TokensToday = (int)Math.Min(int.MaxValue,
                    (long)claude.TokensToday + summary.ClaudeTokens);
                lastClaudeActivity = Math.Max(lastClaudeActivity, summary.ClaudeActivity);
                MergeDomesticFile(domestic.Qwen, summary.QwenTokens,
                    summary.QwenModel, summary.QwenActivity);
                MergeDomesticFile(domestic.Xiaomi, summary.XiaomiTokens,
                    summary.XiaomiModel, summary.XiaomiActivity);
            }

            var stale = _claudeFileCache.Keys.Where(path => !livePaths.Contains(path)).ToArray();
            foreach (var path in stale) _claudeFileCache.Remove(path);
        }

        claude.Status = StatusFromDelta(lastClaudeActivity > 0 ? now - lastClaudeActivity : 1e9);
        var qwenAt = domestic.Qwen.LastActivityEpoch;
        var xiaomiAt = domestic.Xiaomi.LastActivityEpoch;
        var latestDomestic = Math.Max(qwenAt, xiaomiAt);
        domestic.ActiveProvider = latestDomestic <= 0 ? "" : qwenAt >= xiaomiAt ? "qwen" : "xiaomi";
        domestic.Status = StatusFromDelta(latestDomestic > 0 ? now - latestDomestic : 1e9);
        return claude;
    }

    static void MergeDomesticFile(DomesticProviderStatus target, long tokens,
                                  string model, double activity)
    {
        target.TokensToday += tokens;
        if (activity >= target.LastActivityEpoch)
        {
            target.LastActivityEpoch = activity;
            target.Model = model;
        }
    }

    static ClaudeFileSummary ParseClaudeFile(string[] lines, double todayStart, double mtime)
    {
        long claudeTokens = 0, qwenTokens = 0, xiaomiTokens = 0;
        double claudeAt = 0, qwenAt = 0, xiaomiAt = 0;
        string qwenModel = "", xiaomiModel = "";
        foreach (var line in lines)
        {
            if (!line.Contains("\"usage\":{")) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!TryProp(root, "message", out var message)
                    || !TryProp(message, "usage", out var usage)) continue;
                var entryEpoch = ParseIso(StringVal(root, "timestamp"));
                if (entryEpoch.HasValue && entryEpoch.Value < todayStart) continue;
                var model = StringVal(message, "model") ?? "";
                var provider = ModelProvider(model);
                if (provider.Length == 0) continue;
                var tokens = (long)IntVal(usage, "input_tokens") + IntVal(usage, "output_tokens")
                    + IntVal(usage, "cache_creation_input_tokens")
                    + IntVal(usage, "cache_read_input_tokens");
                var activity = entryEpoch ?? mtime;
                if (provider == "claude")
                {
                    claudeTokens += tokens;
                    claudeAt = Math.Max(claudeAt, activity);
                }
                else if (provider == "qwen")
                {
                    qwenTokens += tokens;
                    if (activity >= qwenAt) { qwenAt = activity; qwenModel = model; }
                }
                else
                {
                    xiaomiTokens += tokens;
                    if (activity >= xiaomiAt) { xiaomiAt = activity; xiaomiModel = model; }
                }
            }
        }
        return new ClaudeFileSummary(claudeTokens, claudeAt,
            qwenTokens, qwenModel, qwenAt, xiaomiTokens, xiaomiModel, xiaomiAt);
    }

    // MARK: - Codex

    CodexStatus ReadCodex()
    {
        var now = Now();
        double lastMtime = 0;
        double latestTaskComplete = 0;

        // Whole-tree scan just for the freshest mtime (drives working/idle).
        if (Directory.Exists(_codexDir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_codexDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)
                        .ToUnixTimeMilliseconds() / 1000.0;
                    if (mtime > lastMtime) lastMtime = mtime;
                    if (mtime >= _startedAt)
                        latestTaskComplete = Math.Max(latestTaskComplete,
                            LatestCodexTaskComplete(file, _startedAt));
                }
            }
            catch
            {
                // partial scan is fine
            }
        }

        if (latestTaskComplete > _codexCompletionObservedAt)
        {
            var completionChanged = SetCodexCompletion(latestTaskComplete);
            _codexEvent = new AgentEvent("idle", latestTaskComplete);
            _codexNeedsInputAt = null;
            if (completionChanged) QueueCallback(UrgentUpdate);
        }

        // Tokens + rate limits only from today's day directory.
        var today = DateTime.Today;
        var dayDir = Path.Combine(_codexDir, $"{today.Year:D4}", $"{today.Month:D2}", $"{today.Day:D2}");

        var tokensToday = 0;
        JsonElement? latestRateLimits = null;
        JsonDocument latestRateLimitsDoc = null;
        double latestRateLimitsTs = 0;

        if (Directory.Exists(dayDir))
        {
            foreach (var file in Directory.EnumerateFiles(dayDir, "*.jsonl"))
            {
                var lines = ReadLines(file);
                if (lines == null) continue;
                var sessionMaxTokens = 0;
                foreach (var line in lines)
                {
                    if (!line.Contains("\"token_count\"")) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); } catch { continue; }
                    var root = doc.RootElement;
                    if (!TryProp(root, "payload", out var payload)
                        || StringVal(payload, "type") != "token_count")
                    {
                        doc.Dispose();
                        continue;
                    }
                    if (TryProp(payload, "info", out var info)
                        && TryProp(info, "total_token_usage", out var totalUsage))
                    {
                        var total = IntVal(totalUsage, "total_tokens");
                        if (total > sessionMaxTokens) sessionMaxTokens = total;
                    }
                    if (TryProp(payload, "rate_limits", out var rl))
                    {
                        var e = ParseIso(StringVal(root, "timestamp")) ?? 0;
                        if (e >= latestRateLimitsTs)
                        {
                            latestRateLimitsTs = e;
                            latestRateLimitsDoc?.Dispose();
                            latestRateLimitsDoc = doc; // keep doc alive for rl
                            latestRateLimits = rl;
                            continue;
                        }
                    }
                    doc.Dispose();
                }
                tokensToday += sessionMaxTokens;
            }
        }

        var s = new CodexStatus { TokensToday = tokensToday };
        s.Status = StatusFromDelta(lastMtime > 0 ? now - lastMtime : 1e9);
        if (latestRateLimits.HasValue)
        {
            var rl = latestRateLimits.Value;
            if (TryProp(rl, "primary", out var primary))
                AssignCodexWindow(s, primary, now, false);
            if (TryProp(rl, "secondary", out var secondary))
                AssignCodexWindow(s, secondary, now, true);
        }
        latestRateLimitsDoc?.Dispose();
        return s;
    }

    static void AssignCodexWindow(CodexStatus status, JsonElement window,
                                  double now, bool weeklyFallback)
    {
        var minutes = DoubleVal(window, "window_minutes");
        var weekly = minutes.HasValue ? minutes.Value >= 2 * 24 * 60 : weeklyFallback;
        var pct = DoubleVal(window, "used_percent");
        var reset = DoubleVal(window, "resets_at");
        var resetMin = reset.HasValue ? Math.Max(0, (int)((reset.Value - now) / 60)) : (int?)null;
        if (weekly)
        {
            status.WeeklyPct = pct;
            status.WeeklyWindowMin = (int?)minutes;
            status.WeeklyResetMin = resetMin;
        }
        else
        {
            status.PrimaryPct = pct;
            status.PrimaryWindowMin = (int?)minutes;
            status.PrimaryResetMin = resetMin;
        }
    }
}
