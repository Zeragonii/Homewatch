using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace HomeWatchAgent;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new AgentContext());
    }
}

public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "";
    public string InstallationId { get; set; } = Guid.NewGuid().ToString();
    public string EnrollmentSecret { get; set; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public string? DeviceId { get; set; }
    public string? DeviceToken { get; set; }
}

public sealed class AgentContext : ApplicationContext
{
    static readonly string AgentVersion = GetAgentVersion();
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeWatch");
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly NotifyIcon tray;
    AgentConfig config = new();
    readonly Dictionary<string, int> usage = new(StringComparer.OrdinalIgnoreCase);
    string currentApp = "";
    DateTime lastSample = DateTime.UtcNow;
    DateTime lastHeartbeat = DateTime.MinValue;
    DateTime lastActivityFlush = DateTime.MinValue;
    DateTime lastCommandPoll = DateTime.MinValue;
    DateTime lastUpdateCheck = DateTime.MinValue;
    DateTime lastPolicyCheck = DateTime.MinValue;
    DateTime lastPolicyEnforcement = DateTime.MinValue;
    string lastPolicyNotice = "";

    public AgentContext()
    {
        Directory.CreateDirectory(dataDir);
        tray = new NotifyIcon { Icon = SystemIcons.Shield, Visible = true, Text = "HomeWatch Agent" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Status", null, (_, _) => ShowStatus());
        menu.Items.Add("Exit", null, (_, _) => { tray.Visible = false; Application.Exit(); });
        tray.ContextMenuStrip = menu;

        LoadOrCreateConfig();
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            using var setup = new ServerSetupForm();
            if (setup.ShowDialog() != DialogResult.OK) { tray.Visible = false; ExitThread(); return; }
            config.ServerUrl = setup.ServerUrl.TrimEnd('/'); SaveConfig();
        }
        http.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        ConfigureAutostart();
        _ = LoopAsync();
    }

    void LoadOrCreateConfig()
    {
        var path = Path.Combine(dataDir, "config.json");
        if (File.Exists(path)) config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path)) ?? new AgentConfig();
        SaveConfig();
    }

    void SaveConfig() => File.WriteAllText(Path.Combine(dataDir, "config.json"), JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

    void ConfigureAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        key?.SetValue("HomeWatchAgent", $"\"{Environment.ProcessPath}\"");
    }

    async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                TrackForeground();
                if (string.IsNullOrEmpty(config.DeviceId) || string.IsNullOrEmpty(config.DeviceToken)) await EnsureEnrolledAsync();
                else
                {
                    if (DateTime.UtcNow - lastHeartbeat > TimeSpan.FromSeconds(15)) await HeartbeatAsync();
                    if (DateTime.UtcNow - lastActivityFlush > TimeSpan.FromMinutes(1)) await FlushActivityAsync();
                    if (DateTime.UtcNow - lastCommandPoll > TimeSpan.FromSeconds(3)) await PollCommandsAsync();
                    if (DateTime.UtcNow - lastPolicyCheck > TimeSpan.FromSeconds(20)) await CheckPolicyAsync();
                    if (DateTime.UtcNow - lastUpdateCheck > TimeSpan.FromMinutes(15)) await CheckUpdateAsync();
                }
            }
            catch (Exception ex) { Log($"Loop error: {ex.Message}"); }
            await Task.Delay(1000);
        }
    }

    async Task EnsureEnrolledAsync()
    {
        var body = new { installation_id = config.InstallationId, enrollment_secret = config.EnrollmentSecret, hostname = Environment.MachineName, os_version = Environment.OSVersion.VersionString, agent_version = AgentVersion };
        await http.PostAsJsonAsync("agent/enrol/request", body);
        var claim = await http.PostAsJsonAsync("agent/enrol/claim", new { installation_id = config.InstallationId, enrollment_secret = config.EnrollmentSecret });
        if (!claim.IsSuccessStatusCode) return;
        using var json = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        if (json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "approved")
        {
            config.DeviceId = json.RootElement.GetProperty("device_id").GetString();
            config.DeviceToken = json.RootElement.GetProperty("device_token").GetString();
            SaveConfig();
            tray.ShowBalloonTip(3000, "HomeWatch", "This PC has been enrolled.", ToolTipIcon.Info);
        }
        await Task.Delay(5000);
    }

    HttpRequestMessage Request(HttpMethod method, string url, HttpContent? content = null)
    {
        var r = new HttpRequestMessage(method, url) { Content = content };
        r.Headers.Add("X-Device-ID", config.DeviceId);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.DeviceToken);
        return r;
    }

    async Task HeartbeatAsync()
    {
        using var content = JsonContent.Create(new { hostname = Environment.MachineName, os_version = Environment.OSVersion.VersionString, agent_version = AgentVersion, current_app = currentApp, logged_in_user = LoggedInUser() });
        using var r = Request(HttpMethod.Post, "agent/heartbeat", content);
        await http.SendAsync(r); lastHeartbeat = DateTime.UtcNow;
    }

    void TrackForeground()
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Max(0, (int)(now - lastSample).TotalSeconds);
        if (!string.IsNullOrWhiteSpace(currentApp) && elapsed > 0 && IdleSeconds() < 300) usage[currentApp] = usage.GetValueOrDefault(currentApp) + Math.Min(elapsed, 5);
        currentApp = ForegroundProcessName(); lastSample = now;
    }

    async Task FlushActivityAsync()
    {
        if (usage.Count == 0) { lastActivityFlush = DateTime.UtcNow; return; }
        var snapshot = new Dictionary<string, int>(usage); usage.Clear();
        using var content = JsonContent.Create(new { usage = snapshot });
        using var r = Request(HttpMethod.Post, "agent/activity", content);
        var response = await http.SendAsync(r);
        if (!response.IsSuccessStatusCode) foreach (var kv in snapshot) usage[kv.Key] = usage.GetValueOrDefault(kv.Key) + kv.Value;
        lastActivityFlush = DateTime.UtcNow;
    }

    async Task CheckPolicyAsync()
    {
        lastPolicyCheck = DateTime.UtcNow;
        using var r = Request(HttpMethod.Get, "agent/policy");
        var response = await http.SendAsync(r);
        if (!response.IsSuccessStatusCode) return;
        var policy = JsonSerializer.Deserialize<PolicyState>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (policy?.Enabled != true) { lastPolicyNotice = ""; return; }

        if (policy.App?.Blocked == true && !string.IsNullOrWhiteSpace(policy.App.ProcessName))
        {
            var signature = $"app:{policy.App.ProcessName}";
            if (lastPolicyNotice != signature)
            {
                tray.ShowBalloonTip(5000, "HomeWatch", $"Time is up for {policy.App.ProcessName}. The app will be closed.", ToolTipIcon.Warning);
                lastPolicyNotice = signature;
            }
            await CloseLimitedAppAsync(policy.App.ProcessName);
            return;
        }

        if (policy.Blocked)
        {
            var signature = $"blocked:{policy.Reason}";
            if (lastPolicyNotice != signature)
            {
                tray.ShowBalloonTip(5000, "HomeWatch", string.IsNullOrWhiteSpace(policy.Reason) ? "Screen time is currently unavailable." : policy.Reason, ToolTipIcon.Warning);
                lastPolicyNotice = signature;
            }
            if (DateTime.UtcNow - lastPolicyEnforcement > TimeSpan.FromSeconds(30))
            {
                LockWorkStation();
                lastPolicyEnforcement = DateTime.UtcNow;
            }
            return;
        }

        if (policy.Status is "warning" or "grace")
        {
            var mins = policy.RemainingSeconds.HasValue ? Math.Max(0, (int)Math.Ceiling(policy.RemainingSeconds.Value / 60.0)) : 0;
            var signature = $"{policy.Status}:{mins}";
            if (lastPolicyNotice != signature && (mins <= 10 || policy.Status == "grace"))
            {
                var text = policy.Status == "grace" ? "Your normal screen-time allowance has ended. Grace time is active." : $"About {mins} minute(s) of screen time remaining.";
                tray.ShowBalloonTip(5000, "HomeWatch", text, ToolTipIcon.Warning);
                lastPolicyNotice = signature;
            }
        }
        else lastPolicyNotice = "";
    }

    async Task CloseLimitedAppAsync(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HomeWatchAgent", "HomeWatchUpdater", "explorer", "winlogon", "csrss", "lsass", "services", "smss", "dwm" };
        if (blocked.Contains(name)) return;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                if (process.CloseMainWindow()) { if (!process.WaitForExit(2500)) process.Kill(entireProcessTree:true); }
                else process.Kill(entireProcessTree:true);
            }
            catch (Exception ex) { Log($"Policy could not close {name}: {ex.Message}"); }
            finally { process.Dispose(); }
        }
        await Task.CompletedTask;
    }

    async Task PollCommandsAsync()
    {
        using var r = Request(HttpMethod.Get, "agent/commands");
        var response = await http.SendAsync(r); lastCommandPoll = DateTime.UtcNow;
        if (!response.IsSuccessStatusCode) return;
        var commands = JsonSerializer.Deserialize<List<AgentCommand>>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var cmd in commands)
        {
            try
            {
                if (cmd.Kind == "message")
                {
                    var message = ParseMessagePayload(cmd.Payload);
                    if (message.Type == "notify")
                    {
                        tray.ShowBalloonTip(8000, "HomeWatch · Message", message.Text, ToolTipIcon.Info);
                        await CompleteAsync(cmd.Id, "notification delivered");
                    }
                    else
                    {
                        _ = HandleInteractiveMessageAsync(cmd.Id, message);
                    }
                }
                else if (cmd.Kind == "screenshot") await ScreenshotAsync(cmd.Id);
                else if (cmd.Kind == "check_update") await CheckUpdateAsync(true, cmd.Id);
                else if (cmd.Kind == "lock") await LockAsync(cmd.Id);
                else if (cmd.Kind == "logoff") await LogoffAsync(cmd.Id);
                else if (cmd.Kind == "restart") await PowerAsync(cmd.Id, restart: true);
                else if (cmd.Kind == "shutdown") await PowerAsync(cmd.Id, restart: false);
                else if (cmd.Kind == "close_app") await CloseAppAsync(cmd.Id, cmd.Payload);
                else await CompleteAsync(cmd.Id, "unsupported command");
            }
            catch (Exception ex)
            {
                Log($"Command {cmd.Kind} failed: {ex.Message}");
                try { await CompleteAsync(cmd.Id, $"failed: {ex.Message}"); } catch { }
            }
        }
    }

    MessagePayload ParseMessagePayload(string payload)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<MessagePayload>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Text) && parsed.Type is "notify" or "question" or "alert") return parsed;
        }
        catch { }
        return new MessagePayload { Type = "notify", Text = payload ?? "" };
    }

    Task HandleInteractiveMessageAsync(int commandId, MessagePayload message)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new HomeWatchMessageForm(message.Type, message.Text);
                Application.Run(form);
                tcs.TrySetResult(message.Type == "question" ? $"Reply: {form.ResponseText}" : "Acknowledged");
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return CompleteInteractiveMessageWhenReadyAsync(commandId, tcs.Task);
    }

    async Task CompleteInteractiveMessageWhenReadyAsync(int commandId, Task<string> resultTask)
    {
        try { await CompleteAsync(commandId, await resultTask); }
        catch (Exception ex)
        {
            Log($"Interactive message {commandId} failed: {ex.Message}");
            try { await CompleteAsync(commandId, $"failed: {ex.Message}"); } catch { }
        }
    }

    async Task LockAsync(int id)
    {
        if (!LockWorkStation()) { await CompleteAsync(id, "failed: LockWorkStation returned false"); return; }
        await CompleteAsync(id, "workstation locked");
    }

    async Task LogoffAsync(int id)
    {
        tray.ShowBalloonTip(3000, "HomeWatch", "This Windows session will be logged off in 5 seconds.", ToolTipIcon.Warning);
        await CompleteAsync(id, "logoff scheduled in 5 seconds");
        await Task.Delay(TimeSpan.FromSeconds(5));
        Process.Start(new ProcessStartInfo("shutdown.exe", "/l") { UseShellExecute = false, CreateNoWindow = true });
    }

    async Task PowerAsync(int id, bool restart)
    {
        var action = restart ? "restart" : "shutdown";
        var flag = restart ? "/r" : "/s";
        var args = $"{flag} /t 10 /c \"HomeWatch: {action} requested by parent\"";
        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
        if (p is null) { await CompleteAsync(id, $"failed to start {action}"); return; }
        await CompleteAsync(id, $"{action} scheduled in 10 seconds");
    }

    async Task CloseAppAsync(int id, string payload)
    {
        var raw = (payload ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) { await CompleteAsync(id, "failed: process name required"); return; }
        var processName = Path.GetFileNameWithoutExtension(raw);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HomeWatchAgent", "HomeWatchUpdater", "explorer", "winlogon", "csrss", "lsass", "services", "smss", "dwm" };
        if (blocked.Contains(processName)) { await CompleteAsync(id, $"refused protected process: {processName}"); return; }
        var matches = Process.GetProcessesByName(processName);
        if (matches.Length == 0) { await CompleteAsync(id, $"process not running: {processName}"); return; }
        var closed = 0;
        foreach (var process in matches)
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                if (process.CloseMainWindow())
                {
                    if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
                }
                else process.Kill(entireProcessTree: true);
                closed++;
            }
            catch (Exception ex) { Log($"Could not close {processName} PID {process.Id}: {ex.Message}"); }
            finally { process.Dispose(); }
        }
        await CompleteAsync(id, closed > 0 ? $"closed {closed} process(es): {processName}" : $"failed to close: {processName}");
    }

    async Task CompleteAsync(int id, string result)
    {
        using var content = JsonContent.Create(new { result });
        using var r = Request(HttpMethod.Post, $"agent/commands/{id}/complete", content);
        await http.SendAsync(r);
    }

    async Task ScreenshotAsync(int id)
    {
        var bounds = Screen.AllScreens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        using var ms = new MemoryStream(); bitmap.Save(ms, ImageFormat.Png); ms.Position = 0;
        using var form = new MultipartFormDataContent(); var part = new StreamContent(ms); part.Headers.ContentType = new MediaTypeHeaderValue("image/png"); form.Add(part, "image", "screen.png");
        using var r = Request(HttpMethod.Post, $"agent/commands/{id}/screenshot", form);
        await http.SendAsync(r);
    }

    async Task CheckUpdateAsync(bool forceRefresh = false, int? commandId = null)
    {
        lastUpdateCheck = DateTime.UtcNow;
        using var r = Request(HttpMethod.Get, forceRefresh ? "agent/update?refresh=true" : "agent/update");
        var response = await http.SendAsync(r);
        if (!response.IsSuccessStatusCode)
        {
            if (commandId.HasValue) await CompleteAsync(commandId.Value, $"update check failed: HTTP {(int)response.StatusCode}");
            return;
        }
        var m = JsonSerializer.Deserialize<UpdateManifest>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (m?.Available != true || !Version.TryParse(m.Version, out var target) || !Version.TryParse(AgentVersion, out var current) || target <= current)
        {
            if (commandId.HasValue) await CompleteAsync(commandId.Value, $"already current ({AgentVersion})");
            return;
        }
        Log($"Update available: {AgentVersion} -> {m.Version}");
        var bytes = await http.GetByteArrayAsync(m.Url);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!hash.Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Log("Update rejected: SHA-256 mismatch");
            if (commandId.HasValue) await CompleteAsync(commandId.Value, "update rejected: SHA-256 mismatch");
            return;
        }
        var zip = Path.Combine(dataDir, $"update-{m.Version}.zip"); await File.WriteAllBytesAsync(zip, bytes);
        var installedUpdater = Path.Combine(AppContext.BaseDirectory, "HomeWatchUpdater.exe");
        if (!File.Exists(installedUpdater))
        {
            if (commandId.HasValue) await CompleteAsync(commandId.Value, "updater executable missing");
            return;
        }
        if (commandId.HasValue) await CompleteAsync(commandId.Value, $"update {m.Version} staged");
        var tempUpdater = Path.Combine(Path.GetTempPath(), $"HomeWatchUpdater-{Guid.NewGuid():N}.exe"); File.Copy(installedUpdater, tempUpdater);
        Process.Start(new ProcessStartInfo(tempUpdater, $"{Environment.ProcessId} \"{AppContext.BaseDirectory.TrimEnd('\\')}\" \"{zip}\"") { UseShellExecute = true });
        Log($"Update {m.Version} staged; handing off to updater");
        tray.Visible = false; Application.Exit();
    }

    void ShowStatus() => MessageBox.Show($"Server: {config.ServerUrl}\nDevice: {config.DeviceId ?? "Awaiting approval"}\nVersion: {AgentVersion}\nUser: {LoggedInUser()}\nCurrent app: {currentApp}", "HomeWatch Agent");

    static string GetAgentVersion()
    {
        var info = typeof(AgentContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
        return typeof(AgentContext).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    static string LoggedInUser()
    {
        var domain = Environment.UserDomainName;
        var user = Environment.UserName;
        return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
    }

    void Log(string message)
    {
        try
        {
            var path = Path.Combine(dataDir, "agent.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var now = GetTickCount64();
        return Math.Max(0, (now - info.dwTime) / 1000.0);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    static string ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow(); if (hwnd == IntPtr.Zero) return "";
        GetWindowThreadProcessId(hwnd, out uint pid); if (pid == 0) return "";
        try { return Process.GetProcessById((int)pid).ProcessName + ".exe"; } catch { return ""; }
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

public record AgentCommand(int Id, string Kind, string Payload);
public sealed class MessagePayload { public string Type { get; set; } = "notify"; public string Text { get; set; } = ""; }
public sealed class PolicyState { public bool Enabled { get; set; } public string Status { get; set; } = ""; public bool Blocked { get; set; } public string Reason { get; set; } = ""; public int? RemainingSeconds { get; set; } public PolicyAppState? App { get; set; } }
public sealed class PolicyAppState { public string ProcessName { get; set; } = ""; public bool Blocked { get; set; } public int UsedSeconds { get; set; } public int LimitSeconds { get; set; } }
public sealed class UpdateManifest { public bool Available { get; set; } public string Version { get; set; } = ""; public string Url { get; set; } = ""; public string Sha256 { get; set; } = ""; }

public sealed class HomeWatchMessageForm : Form
{
    readonly TextBox? replyBox;
    readonly System.Windows.Forms.Timer foregroundRetry = new() { Interval = 350 };
    bool completed;
    int foregroundAttempts;
    public string ResponseText => replyBox?.Text.Trim() ?? "";

    public HomeWatchMessageForm(string type, string message)
    {
        var isQuestion = type == "question";
        var accent = isQuestion ? Color.FromArgb(142, 93, 223) : Color.FromArgb(225, 118, 54);
        var surface = Color.FromArgb(22, 29, 43);
        var panel = Color.FromArgb(31, 41, 58);
        var input = Color.FromArgb(13, 21, 34);
        var text = Color.FromArgb(238, 243, 251);
        var muted = Color.FromArgb(166, 177, 195);

        Text = isQuestion ? "HomeWatch · Question" : "HomeWatch · Alert";
        Width = 600;
        Height = isQuestion ? 430 : 360;
        MinimumSize = new Size(520, isQuestion ? 400 : 330);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = surface;
        ForeColor = text;
        Font = new Font("Segoe UI", 10F);
        TopMost = true;
        ShowInTaskbar = true;
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = surface,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 6F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, isQuestion ? 156F : 104F));

        var accentBar = new Panel { Dock = DockStyle.Fill, BackColor = accent, Margin = Padding.Empty };
        var title = new Label
        {
            Text = isQuestion ? "QUESTION FROM PARENT" : "IMPORTANT ALERT",
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 17, 24, 0),
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
            ForeColor = accent,
            BackColor = surface,
            Margin = Padding.Empty
        };

        var messagePanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 12, 24, 16),
            BackColor = surface,
            Margin = Padding.Empty
        };
        var body = new TextBox
        {
            Text = message,
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = surface,
            ForeColor = text,
            Font = new Font("Segoe UI", 11F),
            ScrollBars = ScrollBars.Vertical,
            TabStop = false,
            ShortcutsEnabled = true
        };
        messagePanel.Controls.Add(body);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = panel,
            Padding = new Padding(24, 12, 24, 18),
            Margin = Padding.Empty
        };

        var button = new Button
        {
            Text = isQuestion ? "Send reply" : "Acknowledge",
            Dock = DockStyle.Fill,
            BackColor = accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0)
        };
        button.FlatAppearance.BorderSize = 0;

        if (isQuestion)
        {
            footer.RowCount = 3;
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));

            var prompt = new Label
            {
                Text = "Reply",
                Dock = DockStyle.Fill,
                ForeColor = muted,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            };
            replyBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                MaxLength = 1000,
                BackColor = input,
                ForeColor = text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10.5F),
                Margin = new Padding(0, 0, 0, 10)
            };
            footer.Controls.Add(prompt, 0, 0);
            footer.Controls.Add(replyBox, 0, 1);
            footer.Controls.Add(button, 0, 2);
            AcceptButton = button;
            button.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(replyBox.Text))
                {
                    System.Media.SystemSounds.Exclamation.Play();
                    ForceForeground();
                    replyBox.Focus();
                    return;
                }
                completed = true;
                Close();
            };
        }
        else
        {
            footer.RowCount = 2;
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));

            var note = new Label
            {
                Text = "Please acknowledge this message to continue.",
                Dock = DockStyle.Fill,
                ForeColor = muted,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            };
            footer.Controls.Add(note, 0, 0);
            footer.Controls.Add(button, 0, 1);
            AcceptButton = button;
            button.Click += (_, _) => { completed = true; Close(); };
        }

        root.Controls.Add(accentBar, 0, 0);
        root.Controls.Add(title, 0, 1);
        root.Controls.Add(messagePanel, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);

        FormClosing += (_, e) => { if (!completed) e.Cancel = true; };
        Shown += (_, _) =>
        {
            ForceForeground();
            if (isQuestion) replyBox?.Focus();
            foregroundRetry.Start();
        };
        Activated += (_, _) => NativeMethods.PinTopmost(Handle);
        foregroundRetry.Tick += (_, _) =>
        {
            foregroundAttempts++;
            ForceForeground();
            if (foregroundAttempts >= 4) foregroundRetry.Stop();
        };
    }

    void ForceForeground()
    {
        if (!IsHandleCreated) return;
        NativeMethods.PinTopmost(Handle);
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(Handle);
        Activate();
        BringToFront();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foregroundRetry.Dispose();
        base.Dispose(disposing);
    }
}

internal static class NativeMethods
{
    internal const int SW_RESTORE = 9;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    internal static void PinTopmost(IntPtr hWnd) =>
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
}

public sealed class ServerSetupForm : Form
{
    readonly TextBox box = new() { Dock = DockStyle.Top, PlaceholderText = "https://homewatch.example.com", Margin = new Padding(10) };
    public string ServerUrl => box.Text.Trim();
    public ServerSetupForm()
    {
        Text = "HomeWatch setup"; Width = 480; Height = 170; StartPosition = FormStartPosition.CenterScreen;
        var label = new Label { Text = "Enter your HomeWatch server address:", Dock = DockStyle.Top, Height = 32, Padding = new Padding(0,8,0,0) };
        var button = new Button { Text = "Connect", Dock = DockStyle.Bottom, Height = 38 };
        button.Click += (_, _) => { if (Uri.TryCreate(ServerUrl, UriKind.Absolute, out _)) { DialogResult = DialogResult.OK; Close(); } };
        Controls.Add(button); Controls.Add(box); Controls.Add(label); AcceptButton = button;
    }
}
