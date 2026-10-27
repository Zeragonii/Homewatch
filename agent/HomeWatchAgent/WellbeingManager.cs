using System.Drawing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace HomeWatchAgent;

// Per-Windows-user, per-device wellbeing scheduler. Runs from the tray agent,
// independent of the Launcher window and of screen-time enforcement.
internal sealed class WellbeingManager : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<HttpMethod, string, HttpContent?, HttpRequestMessage> authenticatedRequest;
    private readonly Func<bool> activeConsole;
    private readonly Func<bool> enrolled;
    private readonly Func<double> idleSeconds;
    private readonly Action<string> log;
    private readonly string stateFile;
    private readonly System.Windows.Forms.Timer timer;
    private WellbeingSaved saved = new();
    private List<WellbeingRule> rules = new();
    private DateTime lastTick = DateTime.UtcNow;
    private DateTime lastFetch = DateTime.MinValue;
    private DateTime lastPersist = DateTime.MinValue;
    private bool fetching;
    private bool flushing;
    private DateTime lastEventFlush = DateTime.MinValue;
    private bool showing;
    private bool sessionLocked;
    private bool disposed;
    private DateTime lastPopup = DateTime.MinValue;

    public WellbeingManager(HttpClient http,
        Func<HttpMethod, string, HttpContent?, HttpRequestMessage> authenticatedRequest,
        Func<bool> activeConsole, Func<bool> enrolled, Func<double> idleSeconds,
        string localDirectory, Action<string> log)
    {
        this.http = http;
        this.authenticatedRequest = authenticatedRequest;
        this.activeConsole = activeConsole;
        this.enrolled = enrolled;
        this.idleSeconds = idleSeconds;
        this.log = log;
        stateFile = Path.Combine(localDirectory, "wellbeing-state.json");
        try
        {
            if (File.Exists(stateFile))
            {
                saved = JsonSerializer.Deserialize<WellbeingSaved>(File.ReadAllText(stateFile)) ?? new();
                rules = saved.CachedRules ?? new();
                saved.Progress ??= new();
                saved.PendingEvents ??= new();
            }
        }
        catch (Exception ex) { log("Wellbeing state recovery: " + ex.Message); saved = new(); }
        timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += OnTimer;
        try { SystemEvents.SessionSwitch += OnSessionSwitch; }
        catch (Exception ex) { log("Wellbeing session-event registration: " + ex.Message); }
        timer.Start();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            sessionLocked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            sessionLocked = false;
    }

    private void OnTimer(object? sender, EventArgs args)
    {
        if (disposed) return;
        try { Tick(); }
        catch (Exception ex) { log("Wellbeing scheduler: " + ex.Message); }
    }

    // Sampling is entirely synchronous and never waits for network requests.
    // A server outage must not steal active minutes from the user's timer.
    private void Tick()
    {
        var now = DateTime.UtcNow;
        // Do not accidentally accumulate hours of activity during sleep, suspend,
        // breakpoint pauses, or an unresponsive Windows message loop.
        double dt = Math.Clamp((now - lastTick).TotalSeconds, 0, 5);
        lastTick = now;
        bool console = activeConsole() && !sessionLocked;
        double idle = console ? idleSeconds() : double.MaxValue;

        if (enrolled() && !fetching && now - lastFetch > TimeSpan.FromSeconds(45))
        {
            lastFetch = now;
            _ = PollRulesAsync();
        }
        foreach (var r in rules.Where(r => r.Enabled))
        {
            var state = GetState(r);
            // A snoozed reminder is awaiting a retry; don't also earn the
            // next full interval while the same prompt is deferred.
            if (state.SnoozePending) continue;
            if (r.Mode == "active")
            {
                if (console && idle < r.IdleThresholdSeconds) state.Seconds += dt;
            }
            else if (r.Mode == "clock" && console)
                state.Seconds += dt; // logged-in, unlocked clock time (includes ordinary idle)
        }

        // An unattended screen cannot acknowledge a prompt. Wait for input to
        // resume before showing one; timers keep their earned progress.
        if (console && idle < 180 && !showing && now - lastPopup > TimeSpan.FromSeconds(12))
        {
            foreach (var rule in rules.Where(r => r.Enabled))
            {
                if (!IsDue(rule, GetState(rule), now)) continue;
                ShowReminder(rule, now);
                break; // stagger reminders instead of showing two overlapping dialogs
            }
        }
        if (now - lastPersist > TimeSpan.FromSeconds(20)) Save();
        if (enrolled() && !flushing && saved.PendingEvents.Count > 0
            && now - lastEventFlush > TimeSpan.FromSeconds(20))
        {
            lastEventFlush = now;
            _ = FlushEventsAsync();
        }
    }

    private async Task PollRulesAsync()
    {
        fetching = true;
        try { await RefreshRulesAsync(); }
        catch (Exception ex) { log("Wellbeing policy refresh: " + ex.Message); }
        finally { fetching = false; }
    }

    private static string RuleSignature(WellbeingRule r) =>
        r.Mode + "/" + r.IntervalMinutes + "/" + string.Join(",", r.ScheduledTimes);

    private WellbeingProgress GetState(WellbeingRule r)
    {
        if (!saved.Progress.TryGetValue(r.Id, out var state))
        {
            state = new WellbeingProgress();
            saved.Progress[r.Id] = state;
        }
        state.FiredScheduledSlots ??= new();
        var signature = RuleSignature(r);
        if (state.Signature != signature)
        {
            state.Seconds = 0;
            state.SnoozedUntil = null;
            state.SnoozePending = false;
            state.FiredScheduledSlots.Clear();
            state.Signature = signature;
        }
        return state;
    }

    private static bool IsDue(WellbeingRule r, WellbeingProgress state, DateTime nowUtc)
    {
        if (state.SnoozePending)
            return state.SnoozedUntil.HasValue && nowUtc >= state.SnoozedUntil.Value;
        if (r.Mode is "active" or "clock")
            return state.Seconds >= r.IntervalMinutes * 60.0;
        if (r.Mode != "scheduled") return false;
        return CurrentDueSlot(r, state).Length > 0;
    }

    private static string CurrentDueSlot(WellbeingRule rule, WellbeingProgress state)
    {
        var local = DateTime.Now;
        foreach (var t in rule.ScheduledTimes)
        {
            if (!TimeSpan.TryParseExact(t, @"hh\:mm", null, out var scheduled)) continue;
            var when = local.Date + scheduled;
            string key = local.ToString("yyyy-MM-dd") + "T" + t;
            if (when <= local && local - when <= TimeSpan.FromMinutes(30)
                && !state.FiredScheduledSlots.Contains(key)) return key;
        }
        return "";
    }

    private void ShowReminder(WellbeingRule rule, DateTime now)
    {
        showing = true;
        lastPopup = now;
        var state = GetState(rule);
        if (!state.SnoozePending)
        {
            if (rule.Mode == "scheduled")
            {
                string key = CurrentDueSlot(rule, state);
                if (key.Length > 0) state.FiredScheduledSlots.Add(key);
                string today = DateTime.Now.ToString("yyyy-MM-dd") + "T";
                state.FiredScheduledSlots.RemoveWhere(k => !k.StartsWith(today, StringComparison.Ordinal));
            }
            else state.Seconds = 0;
        }
        state.SnoozePending = false;
        state.SnoozedUntil = null;
        EnqueueEvent(rule.Id, "shown");
        Save(); // restore after restart, even if the UI goes away immediately
        _ = RunReminderAsync(rule);
    }

    private async Task RunReminderAsync(WellbeingRule rule)
    {
        string result = "acknowledged";
        try { result = await WellbeingPopup.ShowAsync(rule); }
        catch (Exception ex) { log("Wellbeing popup: " + ex.Message); }
        var state = GetState(rule);
        if (result == "snoozed" && rule.AllowSnooze)
        {
            state.SnoozePending = true;
            state.SnoozedUntil = DateTime.UtcNow.AddMinutes(rule.SnoozeMinutes);
        }
        EnqueueEvent(rule.Id, result == "snoozed" ? "snoozed" : "acknowledged");
        Save();
        showing = false;
    }

    private async Task RefreshRulesAsync()
    {
        using var request = authenticatedRequest(HttpMethod.Get, "agent/wellbeing", null);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        rules = json.RootElement.GetProperty("rules").Deserialize<List<WellbeingRule>>() ?? new();
        saved.CachedRules = rules;
        // Don't delete old rule progress or unreported audit events. IDs never
        // get reused; disabled rules simply stop scheduling.
        Save();
    }

    private void EnqueueEvent(int ruleId, string action)
    {
        saved.PendingEvents.Add(new WellbeingEventDto { EventId = Guid.NewGuid().ToString(), RuleId = ruleId, Event = action });
        if (saved.PendingEvents.Count > 100)
            saved.PendingEvents.RemoveRange(0, saved.PendingEvents.Count - 100);
    }

    private async Task FlushEventsAsync()
    {
        flushing = true;
        try
        {
            foreach (var evt in saved.PendingEvents.Take(10).ToList())
            {
                try
                {
                    using var request = authenticatedRequest(HttpMethod.Post, "agent/wellbeing/events", JsonContent.Create(evt));
                    using var response = await http.SendAsync(request);
                    // A reassigned device can no longer report an old child's rule.
                    if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                    {
                        saved.PendingEvents.Remove(evt);
                        Save();
                    }
                    else break;
                }
                catch { break; }
            }
        }
        finally { flushing = false; }
    }

    private void Save()
    {
        try
        {
            lastPersist = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
            string temp = stateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, stateFile, true);
        }
        catch (Exception ex) { log("Wellbeing state save: " + ex.Message); }
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop(); timer.Dispose();
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        Save();
    }
}

internal sealed class WellbeingRule
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "custom";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "active";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("interval_minutes")] public int IntervalMinutes { get; set; } = 60;
    [JsonPropertyName("idle_threshold_seconds")] public int IdleThresholdSeconds { get; set; } = 180;
    [JsonPropertyName("scheduled_times")] public List<string> ScheduledTimes { get; set; } = new();
    [JsonPropertyName("allow_snooze")] public bool AllowSnooze { get; set; } = true;
    [JsonPropertyName("snooze_minutes")] public int SnoozeMinutes { get; set; } = 5;
}

internal sealed class WellbeingProgress
{
    public double Seconds { get; set; }
    public string Signature { get; set; } = "";
    public HashSet<string> FiredScheduledSlots { get; set; } = new();
    public bool SnoozePending { get; set; }
    public DateTime? SnoozedUntil { get; set; }
}

internal sealed class WellbeingSaved
{
    public Dictionary<int, WellbeingProgress> Progress { get; set; } = new();
    public List<WellbeingEventDto> PendingEvents { get; set; } = new();
    public List<WellbeingRule> CachedRules { get; set; } = new();
}

internal sealed class WellbeingEventDto
{
    [JsonPropertyName("event_id")] public string EventId { get; set; } = "";
    [JsonPropertyName("rule_id")] public int RuleId { get; set; }
    [JsonPropertyName("event")] public string Event { get; set; } = "";
}

internal sealed class WellbeingPopup : Form
{
    private string choice = "acknowledged";
    public string Choice => choice;

    public WellbeingPopup(WellbeingRule rule)
    {
        Text = "HomeWatch · Wellbeing";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(510, 375);
        MinimumSize = new Size(455, 350);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(18, 29, 47);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 11);
        Icon = AgentContext.LoadBrandIcon();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(26, 16, 26, 22) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "HOMEWATCH  ·  TIME FOR YOU", ForeColor = Color.FromArgb(104, 218, 165),
            Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold) }, 0, 0);
        string symbol = rule.Kind == "hydration" ? "💧" : rule.Kind == "toilet" ? "🚽" : "🌟";
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = symbol + "  " + rule.Title,
            Font = new Font("Segoe UI Semibold", 23, FontStyle.Bold), ForeColor = Color.White,
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = rule.Message,
            Font = new Font("Segoe UI", 13), ForeColor = Color.FromArgb(221, 233, 249),
            TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Your game will still be here when you get back. ✨",
            ForeColor = Color.FromArgb(156, 180, 209), Font = new Font("Segoe UI", 9) }, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var done = new Button { Text = "Done!", Width = 175, Height = 45, BackColor = Color.FromArgb(45, 155, 118),
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold) };
        done.FlatAppearance.BorderSize = 0;
        done.Click += (_, _) => { choice = "acknowledged"; Close(); };
        buttons.Controls.Add(done);
        if (rule.AllowSnooze)
        {
            var snooze = new Button { Text = $"In {rule.SnoozeMinutes} minutes", Width = 185, Height = 45,
                BackColor = Color.FromArgb(43, 58, 83), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            snooze.FlatAppearance.BorderSize = 0;
            snooze.Click += (_, _) => { choice = "snoozed"; Close(); };
            buttons.Controls.Add(snooze);
        }
        root.Controls.Add(buttons, 0, 4);
        Controls.Add(root);
        AcceptButton = done;
        Shown += (_, _) => { Activate(); BringToFront(); };
    }

    public static Task<string> ShowAsync(WellbeingRule rule)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new WellbeingPopup(rule);
                Application.Run(form);
                completion.TrySetResult(form.Choice);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
