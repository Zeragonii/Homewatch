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
        if (!string.IsNullOrWhiteSpace(currentApp) && elapsed > 0) usage[currentApp] = usage.GetValueOrDefault(currentApp) + Math.Min(elapsed, 5);
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

    async Task PollCommandsAsync()
    {
        using var r = Request(HttpMethod.Get, "agent/commands");
        var response = await http.SendAsync(r); lastCommandPoll = DateTime.UtcNow;
        if (!response.IsSuccessStatusCode) return;
        var commands = JsonSerializer.Deserialize<List<AgentCommand>>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var cmd in commands)
        {
            if (cmd.Kind == "message")
            {
                MessageBox.Show(cmd.Payload, "Message from parent", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
                await CompleteAsync(cmd.Id, "shown");
            }
            else if (cmd.Kind == "screenshot") await ScreenshotAsync(cmd.Id);
        }
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

    async Task CheckUpdateAsync()
    {
        lastUpdateCheck = DateTime.UtcNow;
        using var r = Request(HttpMethod.Get, "agent/update");
        var response = await http.SendAsync(r); if (!response.IsSuccessStatusCode) return;
        var m = JsonSerializer.Deserialize<UpdateManifest>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (m?.Available != true || !Version.TryParse(m.Version, out var target) || !Version.TryParse(AgentVersion, out var current) || target <= current) return;
        Log($"Update available: {AgentVersion} -> {m.Version}");
        var bytes = await http.GetByteArrayAsync(m.Url);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!hash.Equals(m.Sha256, StringComparison.OrdinalIgnoreCase)) { Log("Update rejected: SHA-256 mismatch"); return; }
        var zip = Path.Combine(dataDir, $"update-{m.Version}.zip"); await File.WriteAllBytesAsync(zip, bytes);
        var installedUpdater = Path.Combine(AppContext.BaseDirectory, "HomeWatchUpdater.exe"); if (!File.Exists(installedUpdater)) return;
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

    static string ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow(); if (hwnd == IntPtr.Zero) return "";
        GetWindowThreadProcessId(hwnd, out uint pid); if (pid == 0) return "";
        try { return Process.GetProcessById((int)pid).ProcessName + ".exe"; } catch { return ""; }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

public record AgentCommand(int Id, string Kind, string Payload);
public sealed class UpdateManifest { public bool Available { get; set; } public string Version { get; set; } = ""; public string Url { get; set; } = ""; public string Sha256 { get; set; } = ""; }

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
