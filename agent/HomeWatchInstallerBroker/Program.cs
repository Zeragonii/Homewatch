using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Runtime.InteropServices;

// A separate privileged *staging* service. This service deliberately never
// starts interactive executables and never exposes a generic command runner.
// Interactive password-free execution is NOT implemented in this milestone.
if (args.Length == 1 && args[0] == "--enrol")
{
    try
    {
        await BrokerSettings.EnrolAsync();
        User32.MessageBox(IntPtr.Zero,
            "The HomeWatch installer broker is configured and ready to verify approved packages.",
            "HomeWatch installer broker", 0x40);
    }
    catch (Exception ex)
    {
        User32.MessageBox(IntPtr.Zero, "Unable to configure installer broker:\n\n" + ex.Message,
            "HomeWatch installer broker", 0x10);
        Environment.ExitCode = 1;
    }
    return;
}
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "HomeWatchInstallerBroker");
builder.Services.AddHostedService<BrokerWorker>();
await builder.Build().RunAsync();

internal static class User32
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}

internal sealed record BrokerConfig(string ServerUrl, string DeviceId, string DeviceToken);

internal static class BrokerSettings
{
    internal const string Pipe = "HomeWatchInstallerBroker";
    internal static readonly string Base = Path.GetFullPath(AppContext.BaseDirectory);
    // Parent directory is inside Program Files (not child-writable ProgramData).
    internal static readonly string Root = Path.Combine(Base, ".installer-broker");
    internal static readonly string Stage = Path.Combine(Root, "staged");
    internal static readonly string Config = Path.Combine(Root, "broker-config.json");
    internal static readonly string Log = Path.Combine(Root, "audit.log");

    internal static BrokerConfig ReadConfig()
    {
        var model = JsonSerializer.Deserialize<BrokerConfig>(File.ReadAllText(Config))
            ?? throw new IOException("Broker configuration is missing");
        if (!Uri.TryCreate(model.ServerUrl, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(model.DeviceId) ||
            string.IsNullOrWhiteSpace(model.DeviceToken))
            throw new UnauthorizedAccessException("The installer broker requires a locally pinned HTTPS server and enrolled device");
        return model;
    }

    internal static async Task EnrolAsync()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Enrolment requires an administrator");
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HomeWatch", "config.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(source));
        var root = doc.RootElement;
        string url = root.GetProperty("ServerUrl").GetString() ?? "";
        string id = root.GetProperty("DeviceId").GetString() ?? "";
        string token = root.GetProperty("DeviceToken").GetString() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token))
            throw new IOException("Enrol the device using HTTPS before provisioning the installer broker");
        // Only the local administrator may establish this trusted server origin.
        // Display the exact host/device instead of silently accepting the
        // normal-user-modifiable HomeWatch device configuration.
        if (User32.MessageBox(IntPtr.Zero,
            "Approve HomeWatch broker enrolment for this server?\n\n" +
            url + "\n\nDevice ID: " + id +
            "\n\nThis will be pinned for all future protected installer requests.",
            "HomeWatch installer broker — verify server", 0x34) != 6)
            throw new OperationCanceledException("Administrator declined server enrolment");
        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("X-Device-ID", id);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var check = await http.GetAsync("agent/catalogue");
        check.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Stage);
        // Only the administrator-approved enrolment captures server identity/credentials.
        // Ordinary user edits to ProgramData\HomeWatch\config.json are ignored hereafter.
        string tmp = Config + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Secure the EMPTY temporary file *before* writing any device
            // credentials, so there is never a user-readable secret window.
            using (File.Create(tmp)) { }
            var acl = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "icacls.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            acl.ArgumentList.Add(tmp);
            acl.ArgumentList.Add("/inheritance:r");
            acl.ArgumentList.Add("/grant:r");
            acl.ArgumentList.Add("*S-1-5-18:F");
            acl.ArgumentList.Add("*S-1-5-32-544:F");
            using var process = Process.Start(acl) ?? throw new IOException("Cannot secure broker configuration ACL");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new IOException("Could not restrict configuration to administrators and SYSTEM");
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(new BrokerConfig(url, id, token)));
            File.Move(tmp, Config, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        Console.WriteLine("Installer broker registered for " + uri.Host + " and enrolled device " + id);
    }
    internal static void Audit(string message)
    {
        try { Directory.CreateDirectory(Root);
            File.AppendAllText(Log, $"{DateTimeOffset.UtcNow:O} {message.Replace('\r',' ').Replace('\n',' ')}{Environment.NewLine}"); }
        catch { }
    }
}

internal sealed class BrokerWorker(ILogger<BrokerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Fail closed on an unconfigured machine: do not accidentally trust the
        // ordinary user's modifiable server URL or enrollment credentials.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                using var pipe = NamedPipeServerStreamAcl.Create(BrokerSettings.Pipe, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, security);
                await pipe.WaitForConnectionAsync(stoppingToken);
                // One request per connection. No request supplies executable path,
                // arguments, destination or administrative commands.
                await HandleAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Rejected installer staging request: {Error}", e.Message);
                BrokerSettings.Audit("rejected " + e.Message); }
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        // Read a bounded request FIRST. ImpersonateNamedPipeClient works on the
        // most recent message read and must not be called before a pipe read.
        var request = new byte[24];
        int bytes = 0;
        while (bytes < request.Length)
        {
            int n = await pipe.ReadAsync(request.AsMemory(bytes, 1), stop);
            if (n != 1) throw new IOException("Client closed pipe before sending a catalogue ID");
            if (request[bytes] == (byte)'\n') break;
            bytes++;
        }
        if (bytes >= request.Length)
            throw new IOException("Installer broker request is too long");
        string line = Encoding.ASCII.GetString(request, 0, bytes).Trim();
        string sid = "";
        pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value ?? "");
        if (!Regex.IsMatch(sid, @"^S-1-5-21-(\d+-){3}\d+$"))
            throw new UnauthorizedAccessException("Installer requests require a local Windows account");
        if (!int.TryParse(line, out int id) || id <= 0)
            throw new IOException("Only a positive catalogue identifier is accepted");
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen:true) { AutoFlush = true };
        try
        {
            var entry = await StageApprovedAsync(sid, id, stop);
            await writer.WriteLineAsync("OK " + entry);
            BrokerSettings.Audit("approved-stage catalogue=" + id + " sid=" + sid);
        }
        catch (Exception e)
        {
            await writer.WriteLineAsync("ERROR " + e.Message.Replace('\r',' ').Replace('\n',' '));
            BrokerSettings.Audit("denied catalogue=" + id + " sid=" + sid + " " + e.Message);
        }
    }

    private static bool SafeRelative(string path) =>
        path.Length is > 0 and <= 700 && !path.Contains('/') && !path.Contains(':') &&
        !path.StartsWith('\\') && path.Split('\\').All(x => x.Length > 0 && x is not ("." or ".."));

    private static void NoReparsePath(string root, string file)
    {
        string current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse point in staged package root");
        var relative = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
        foreach (var segment in relative)
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse point in installer source: " + segment);
        }
    }

    private static async Task<string> StageApprovedAsync(string sid, int id, CancellationToken stop)
    {
        var cfg = BrokerSettings.ReadConfig();
        using var http = new HttpClient { BaseAddress = new Uri(cfg.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Add("X-Device-ID", cfg.DeviceId);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.DeviceToken);
        using var response = await http.GetAsync($"agent/elevation/installer/{id}", stop);
        if (!response.IsSuccessStatusCode) throw new UnauthorizedAccessException("Parent has not approved privileged installation");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop));
        var policy = doc.RootElement;
        var digest = policy.GetProperty("installer_sha256").GetString() ?? "";
        var entry = policy.GetProperty("package_entrypoint").GetString() ?? "";
        if (!Regex.IsMatch(digest, "^[a-f0-9]{64}$") || !SafeRelative(entry))
            throw new IOException("Invalid package fingerprint or entrypoint");
        // Source is derived from Windows' SID -> profile registry, NOT a path
        // submitted by a child-controlled IPC message.
        using var profileKey = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
        string profile = Environment.ExpandEnvironmentVariables(profileKey?.GetValue("ProfileImagePath") as string ?? "");
        if (!Path.IsPathFullyQualified(profile) || profile.StartsWith(@"\\"))
            throw new IOException("Could not verify caller's Windows profile");
        var source = Path.Combine(profile, "AppData", "Local", "HomeWatch", "Packages", id + "-" + digest);
        if (!Directory.Exists(source)) throw new IOException("The package must first be downloaded by HomeWatch Launcher");
        NoReparsePath(source, source);
        var files = policy.GetProperty("manifest").GetProperty("files").EnumerateArray().ToList();
        if (files.Count == 0 || files.Count > 4000) throw new IOException("Invalid package file count");
        Directory.CreateDirectory(BrokerSettings.Stage);
        // Never let a logged-in user exhaust the drive by repeatedly requesting
        // staging of an already approved package. Cleanup is administrator-owned.
        if (Directory.EnumerateDirectories(BrokerSettings.Stage).Take(5).Count() >= 5)
            throw new IOException("Protected staging is full; an administrator must remove old packages");
        long expectedTotal = files.Sum(file => file.GetProperty("size").GetInt64());
        if (expectedTotal > new DriveInfo(Path.GetPathRoot(BrokerSettings.Stage)!).AvailableFreeSpace - 1024L*1024*1024)
            throw new IOException("Insufficient free space for a second, protected installer copy");
        // Unique folder per operation, inherited protected Program Files ACLs.
        string target = Path.Combine(BrokerSettings.Stage, id + "-" + digest + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        try
        {
            foreach (var file in files)
            {
                stop.ThrowIfCancellationRequested();
                string relative = file.GetProperty("path").GetString() ?? "";
                string expected = file.GetProperty("sha256").GetString() ?? "";
                long size = file.GetProperty("size").GetInt64();
                if (!SafeRelative(relative) || !Regex.IsMatch(expected, "^[a-f0-9]{64}$") ||
                    !seen.Add(relative) || size < 0 || size > 300L * 1024 * 1024 * 1024 - total)
                    throw new IOException("Invalid package manifest item");
                total += size;
                var inputPath = Path.GetFullPath(Path.Combine(source, relative));
                NoReparsePath(source, inputPath);
                if (!inputPath.StartsWith(Path.GetFullPath(source) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escaped user staging");
                var outputPath = Path.GetFullPath(Path.Combine(target, relative));
                if (!outputPath.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escaped protected staging");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != size) throw new IOException("File size does not match parent manifest");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[1024 * 1024];
                long count = 0; int n;
                while ((n = await input.ReadAsync(buf, stop)) > 0)
                {
                    count += n;
                    if (count > size) throw new IOException("Package file changed during copy");
                    hash.AppendData(buf, 0, n);
                    await output.WriteAsync(buf.AsMemory(0, n), stop);
                }
                if (count != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected,
                    StringComparison.OrdinalIgnoreCase)) throw new IOException("Package fingerprint mismatch");
            }
            string exe = Path.Combine(target, entry);
            if (!File.Exists(exe)) throw new IOException("Installer executable not found in verified package");
            return exe;
        }
        catch
        {
            try { Directory.Delete(target, true); } catch { }
            throw;
        }
    }
}
