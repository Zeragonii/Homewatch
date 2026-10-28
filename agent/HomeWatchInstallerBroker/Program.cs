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

// Dedicated installer broker: parent-approved, hash-verified packages only.
// Interactive execution uses a dedicated local administrator account, NEVER SYSTEM.
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
if (args.Length == 1 && args[0] == "--remove-runner")
{
    RunnerIdentity.Remove();
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
            throw new IOException("The HomeWatch server URL currently uses HTTP (or the device is not enrolled). " +
                "Configure a trusted HTTPS reverse proxy, then use Administrator performance settings > " +
                "Server connection to save and test the HTTPS URL. Keep ProgramData: do not reset device enrolment.");
        // Only the local administrator may establish this trusted server origin.
        // Display the exact host/device instead of silently accepting the
        // normal-user-modifiable HomeWatch device configuration.
        if (User32.MessageBox(IntPtr.Zero,
            "Approve HomeWatch broker enrolment for this server?\n\n" +
            url + "\n\nDevice ID: " + id +
            "\n\nThis will be pinned for all future protected installer requests.",
            "HomeWatch installer broker — verify server", 0x24) != 6)
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
        // Both trust decisions occur only during this locally elevated setup.
        if (!File.Exists(RunnerIdentity.FilePath))
        {
            if (User32.MessageBox(IntPtr.Zero,
                "Enable password-free interactive installations for parent-approved packages?\n\n" +
                "HomeWatch will create a dedicated LOCAL ADMINISTRATOR account with a random secret " +
                "that stays encrypted on this PC. Approved installer windows will be fully elevated " +
                "and can make unrestricted administrator-level changes to Windows.\n\n" +
                "Only use this with packages you personally trust. This is not a sandbox.\n\n" +
                "Create the dedicated administrator identity?",
                "HomeWatch — approve installer privileges", 0x24) != 6)
                throw new OperationCanceledException("Administrator did not approve password-free installation");
        }
        RunnerIdentity.Provision();
        Console.WriteLine("Installer broker registered for " + uri.Host + " and enrolled device " + id);
    }
    internal static void LockDownFile(string filename)
    {
        var acl = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "icacls.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        acl.ArgumentList.Add(filename);
        acl.ArgumentList.Add("/inheritance:r");
        acl.ArgumentList.Add("/grant:r");
        acl.ArgumentList.Add("*S-1-5-18:F");
        acl.ArgumentList.Add("*S-1-5-32-544:F");
        using var process = Process.Start(acl) ?? throw new IOException("Cannot secure broker identity ACL");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Could not secure broker identity permissions");
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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, InstallationRun> Runs = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> CopyingStages = new();
    private static readonly SemaphoreSlim LaunchLock = new(1, 1);
    private static readonly TimeSpan InstallerExitGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AbandonedStageAge = TimeSpan.FromHours(24);
    private sealed class InstallationRun
    {
        internal required string Sid { get; init; }
        internal required uint Session { get; init; }
        internal volatile bool Complete;
        internal int ExitCode;
        internal required string StagedExe { get; init; }
        internal DateTime FinishedUtc;
    }

    private static async Task MonitorAsync(string ticket, RunnerIdentity.LaunchedInstaller installed, string stagedExe)
    {
        try
        {
            using (installed)
            {
                int code = await Task.Run(installed.WaitForExit);
                if (Runs.TryGetValue(ticket, out var state)) { state.ExitCode = code; state.FinishedUtc = DateTime.UtcNow; state.Complete = true; }
                BrokerSettings.Audit("installer-exit ticket=" + ticket + " code=" + code);
            }
        }
        catch (Exception ex)
        {
            if (Runs.TryGetValue(ticket, out var state)) { state.ExitCode = -1; state.FinishedUtc = DateTime.UtcNow; state.Complete = true; }
            BrokerSettings.Audit("installer-monitor-error ticket=" + ticket + " " + ex.Message);
        }
        finally
        {
            // Keep the status ticket available for a while after process exit.
            // Cleanup is independent of the IPC connection or launcher lifetime.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(InstallerExitGrace);
                    await RetryCleanupAsync(stagedExe);
                }
                finally { Runs.TryRemove(ticket, out _); }
            });
        }
    }

    private static async Task RetryCleanupAsync(string stagedExe)
    {
        // Some setup wizards spawn a second EXE and exit. Do not delete their
        // payload while a process is still running directly from that stage.
        // Locked files also remain for the next retry / service recovery sweep.
        for (int attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                if (!IsStageInUse(stagedExe))
                {
                    CleanupStage(stagedExe);
                    return;
                }
            }
            catch (Exception ex)
            {
                if (attempt == 0 || attempt % 10 == 0)
                    BrokerSettings.Audit("stage-cleanup-retry " + ex.Message);
            }
            await Task.Delay(TimeSpan.FromMinutes(1));
        }
        BrokerSettings.Audit("stage-cleanup-deferred-to-maintenance " + stagedExe);
    }

    private static bool IsStageInUse(string stagedExe)
    {
        string directory = StageDirectory(stagedExe) ?? "";
        if (directory.Length == 0) return true; // fail closed
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName?.StartsWith(directory + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }

    private sealed record StageUsage(long TotalBytes, long EligibleBytes);
    private static readonly TimeSpan UnknownStageGrace = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim CleaningLock = new(1, 1);

    // Stage directory names are minted by the broker and checked against an
    // exact pattern. These commands never accept a path or catalogue ID.
    private static IEnumerable<string> EnumerateStageFolders()
    {
        if (!Directory.Exists(BrokerSettings.Stage) ||
            (File.GetAttributes(BrokerSettings.Stage) & FileAttributes.ReparsePoint) != 0)
            return Array.Empty<string>();
        return Directory.EnumerateDirectories(BrokerSettings.Stage)
            .Where(d => StageName.IsMatch(Path.GetFileName(d)) &&
                (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0).ToArray();
    }

    private static long StageTreeBytes(string path)
    {
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    try
                    {
                        var attrs = File.GetAttributes(entry);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attrs & FileAttributes.Directory) != 0) pending.Push(entry);
                        else bytes = checked(bytes + new FileInfo(entry).Length);
                    }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return bytes;
    }

    private static bool EligibleForManualCleanup(string directory)
    {
        if (CopyingStages.ContainsKey(directory)) return false;
        if (Runs.Values.Any(r => !r.Complete)) return false;
        var matching = Runs.Values.Where(r => string.Equals(StageDirectory(r.StagedExe), directory,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length > 0)
        {
            if (matching.Any(r => !r.Complete ||
                r.FinishedUtc == default || DateTime.UtcNow - r.FinishedUtc < InstallerExitGrace))
                return false;
        }
        else if (Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow - UnknownStageGrace)
            return false;
        // Also checks whether an installer child executable is still running.
        return !IsStageInUse(Path.Combine(directory, "setup.exe"));
    }

    private static StageUsage GetStageUsage()
    {
        long total = 0, eligible = 0;
        foreach (var folder in EnumerateStageFolders())
        {
            try
            {
                long bytes = StageTreeBytes(folder);
                total = checked(total + bytes);
                if (EligibleForManualCleanup(folder)) eligible = checked(eligible + bytes);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new StageUsage(total, eligible);
    }

    private static async Task<long> CleanStagesAsync(CancellationToken token)
    {
        await CleaningLock.WaitAsync(token);
        try
        {
            long before = GetStageUsage().TotalBytes;
            foreach (var folder in EnumerateStageFolders())
            {
                try
                {
                    if (EligibleForManualCleanup(folder))
                        CleanupStage(Path.Combine(folder, "setup.exe"));
                }
                catch (IOException ex) { BrokerSettings.Audit("manual-stage-cleanup-skipped " + ex.Message); }
                catch (UnauthorizedAccessException ex) { BrokerSettings.Audit("manual-stage-cleanup-skipped " + ex.Message); }
            }
            long after = GetStageUsage().TotalBytes;
            long freed = Math.Max(0, before - after);
            BrokerSettings.Audit("manual-stage-cleanup bytes=" + freed);
            return freed;
        }
        finally { CleaningLock.Release(); }
    }

    private static async Task StageMaintenanceAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (Directory.Exists(BrokerSettings.Stage))
                foreach (var directory in Directory.EnumerateDirectories(BrokerSettings.Stage))
                {
                    try
                    {
                        string name = Path.GetFileName(directory);
                        if (!StageName.IsMatch(name) || CopyingStages.ContainsKey(directory) ||
                            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                            Directory.GetCreationTimeUtc(directory) > DateTime.UtcNow - AbandonedStageAge ||
                            Runs.Values.Any(run => !run.Complete)) continue;
                        // Use the validated folder name to build an in-root path;
                        // no caller-controlled cleanup paths are accepted.
                        string placeholder = Path.Combine(directory, "setup.exe");
                        if (!IsStageInUse(placeholder)) CleanupStage(placeholder);
                    }
                    catch (Exception ex) { BrokerSettings.Audit("stage-maintenance-error " + ex.Message); }
                }
            }
            catch (Exception ex) { BrokerSettings.Audit("stage-maintenance-enumeration-error " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static readonly Regex StageName = new(@"^\d+-[a-f0-9]{64}-[a-f0-9]{32}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recovery sweep also runs after service restarts, when any in-memory
        // delayed deletion task from the previous service instance was lost.
        _ = StageMaintenanceAsync(stoppingToken);
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
                var pipe = NamedPipeServerStreamAcl.Create(BrokerSettings.Pipe, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, security);
                await pipe.WaitForConnectionAsync(stoppingToken);
                // One request per connection. No request supplies executable path,
                // arguments, destination or administrative commands.
                // Stage operations can involve hundreds of GB. Keep accepting
                // other status requests while one installation is running.
                _ = Task.Run(async () =>
                {
                    using (pipe) await HandleAsync(pipe, stoppingToken, log);
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Rejected installer staging request: {Error}", e.Message);
                BrokerSettings.Audit("rejected " + e.Message); }
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stop, ILogger<BrokerWorker> log)
    {
        // Create the response writer BEFORE parsing or impersonating. Every
        // rejectable request must receive a structured ERROR reply, otherwise
        // Windows clients only report the misleading "closed pipe" message.
        // The writer does not close the shared duplex pipe before we're done.
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024,
            leaveOpen: true) { AutoFlush = true };
        string sid = "unverified";
        int id = 0;
        try
        {
            // Bound request reading so a client cannot occupy the service pipe
            // forever without sending a complete catalogue identifier.
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            var request = new byte[96];
            int bytes = 0;
            while (bytes < request.Length)
            {
                int n = await pipe.ReadAsync(request.AsMemory(bytes, 1), requestTimeout.Token);
                if (n != 1) throw new IOException("Client disconnected before completing the request");
                if (request[bytes] == (byte)'\n') break;
                bytes++;
            }
            if (bytes >= request.Length)
                throw new IOException("Installer broker request is too long");
            string line = Encoding.ASCII.GetString(request, 0, bytes).Trim();
            // The caller SID must be captured from the Windows pipe impersonation
            // context; never accept a SID supplied in the IPC request.
            try
            {
                pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value ?? "");
            }
            catch (Exception ex)
            {
                throw new UnauthorizedAccessException("Windows could not verify the local requesting account", ex);
            }
            if (!Regex.IsMatch(sid, @"^S-1-5-21-(\d+-){3}\d+$"))
                throw new UnauthorizedAccessException("Installer requests require a local Windows account");
            if (line == "STORAGE")
            {
                var usage = await Task.Run(GetStageUsage, stop);
                await writer.WriteLineAsync($"STORAGE {usage.TotalBytes} {usage.EligibleBytes}");
                return;
            }
            if (line == "CLEAN")
            {
                long freed = await CleanStagesAsync(stop);
                await writer.WriteLineAsync("CLEANED " + freed);
                return;
            }
            if (line == "IDENTITY")
            {
                await writer.WriteLineAsync("ID " + RunnerIdentity.Sid);
                return;
            }
            if (line.StartsWith("STATUS ", StringComparison.Ordinal))
            {
                string ticket = line[7..];
                if (!Guid.TryParseExact(ticket, "N", out _) || !Runs.TryGetValue(ticket, out var state) || state.Sid != sid)
                    throw new UnauthorizedAccessException("Installer operation does not belong to this account");
                await writer.WriteLineAsync(state.Complete ? "EXIT " + state.ExitCode : "RUNNING");
                return;
            }
            bool execute = line.StartsWith("RUN ", StringComparison.Ordinal);
            var catalogue = execute ? line[4..] : line;
            if (!int.TryParse(catalogue, out id) || id <= 0)
                throw new IOException("Only a positive approved catalogue identifier is accepted");
            if (!execute)
            {
                string prepared = await StageApprovedAsync(sid, id, stop);
                // Compatibility with the earlier stage-only protocol. New
                // clients use RUN; older stage-only packages are reaped after
                // an extended timeout or by the restart-recovery sweep.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromHours(6));
                    await RetryCleanupAsync(prepared);
                });
                await writer.WriteLineAsync("OK " + prepared);
                BrokerSettings.Audit("approved-stage catalogue=" + id + " sid=" + sid);
                return;
            }
            RunnerIdentity.EnsureSessionPrivilege();
            if (!NativeSession.TryGetCallerSession(pipe, sid, out uint session))
                throw new UnauthorizedAccessException("Installer launch requires the same active Windows user and session");
            if (Runs.Values.Any(x => !x.Complete))
                throw new IOException("An elevated installation is already active. Finish it before opening another.");
            if (!await LaunchLock.WaitAsync(0, stop))
                throw new IOException("An elevated installer is already being prepared; try again after it starts");
            try
            {
                string entry = await StageApprovedAsync(sid, id, stop);
                bool launched = false;
                try
                {
                    // Revoke permission before a long download completes: a
                    // second pinned-server policy check occurs IMMEDIATELY before launch.
                    await VerifyReadyToLaunchAsync(id, entry, stop);
                    RunnerIdentity.LaunchedInstaller installed = RunnerIdentity.Launch(entry, session);
                    launched = true;
                    string ticket = Guid.NewGuid().ToString("N");
                    Runs[ticket] = new InstallationRun { Sid = sid, Session = session, StagedExe = entry };
                    _ = MonitorAsync(ticket, installed, entry);
                    await writer.WriteLineAsync("STARTED " + ticket);
                    BrokerSettings.Audit("approved-launch catalogue=" + id + " sid=" + sid +
                        " session=" + session + " pid=" + installed.Pid + " ticket=" + ticket);
                }
                catch
                {
                    if (!launched)
                    {
                        // No process is using this protected copy; clean it
                        // immediately on revoked approval or launch failure.
                        try { CleanupStage(entry); }
                        catch (Exception ex) { BrokerSettings.Audit("failed-launch-cleanup " + ex.Message); }
                    }
                    throw;
                }
            }
            finally { LaunchLock.Release(); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Record the underlying exception and stack privately for service
            // troubleshooting. Don't send that privileged diagnostic to the child.
            BrokerSettings.Audit("denied catalogue=" + id + " sid=" + sid +
                " " + ex.GetType().Name + " " + ex.Message);
            log.LogWarning(ex, "Installer broker request failed: catalogue={CatalogueId}, sid={Sid}", id, sid);
            string safeMessage = ex switch
            {
                UnauthorizedAccessException => ex.Message,
                IOException => ex.Message,
                OperationCanceledException => "Installer broker request timed out",
                _ => "Broker could not complete installer staging; check its audit log"
            };
            try
            {
                if (pipe.IsConnected)
                    await writer.WriteLineAsync("ERROR " + safeMessage.Replace('\r', ' ').Replace('\n', ' '));
            }
            catch (IOException) { /* Client disconnected; original failure is logged. */ }
            catch (ObjectDisposedException) { /* Client disconnected. */ }
        }
    }

    private static async Task VerifyReadyToLaunchAsync(int id, string exe, CancellationToken stop)
    {
        var cfg = BrokerSettings.ReadConfig();
        using var http = new HttpClient { BaseAddress = new Uri(cfg.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("X-Device-ID", cfg.DeviceId);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.DeviceToken);
        using var response = await http.GetAsync($"agent/elevation/installer/{id}", stop);
        if (!response.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("Parent withdrew elevated installer approval before launch");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop));
        var root = document.RootElement;
        var entry = root.GetProperty("package_entrypoint").GetString() ?? "";
        if (!SafeRelative(entry)) throw new IOException("Invalid approved setup entrypoint");
        // The protected copy's root has id-sha256-random format.
        string dir = Path.GetDirectoryName(exe)!;
        foreach (var _ in entry.Split('\\').Skip(1)) dir = Path.GetDirectoryName(dir)!;
        var digest = root.GetProperty("installer_sha256").GetString() ?? "";
        if (!Path.GetFileName(dir).StartsWith(id + "-" + digest + "-", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(Path.Combine(dir, entry)).Equals(exe, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Approved installer changed while staging");
        var approved = root.GetProperty("manifest").GetProperty("files").EnumerateArray()
            .FirstOrDefault(f => string.Equals(f.GetProperty("path").GetString(), entry,
                StringComparison.OrdinalIgnoreCase));
        if (approved.ValueKind == JsonValueKind.Undefined)
            throw new IOException("Approved installer entrypoint missing");
        var expectedHash = approved.GetProperty("sha256").GetString() ?? "";
        if (!Regex.IsMatch(expectedHash, "^[a-f0-9]{64}$") ||
            new FileInfo(exe).Length != approved.GetProperty("size").GetInt64())
            throw new IOException("Approved setup executable size changed");
        await using var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, stop));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Approved setup executable hash changed");
    }

    private static string? StageDirectory(string stagedExe)
    {
        string root = Path.GetFullPath(BrokerSettings.Stage);
        string relative = Path.GetRelativePath(root, Path.GetFullPath(stagedExe));
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)) return null;
        string folderName = relative.Split(Path.DirectorySeparatorChar)[0];
        if (!StageName.IsMatch(folderName)) return null;
        string directory = Path.GetFullPath(Path.Combine(root, folderName));
        return directory.StartsWith(root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ? directory : null;
    }

    private static void CleanupStage(string stagedExe)
    {
        string? directory = StageDirectory(stagedExe);
        if (directory is null || !Directory.Exists(directory)) return;
        if (CopyingStages.ContainsKey(directory) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        // Never follow junctions or symlinks, even if the elevated installer
        // created one inside its own private stage.
        DeleteStageTree(directory);
        BrokerSettings.Audit("cleaned-stage " + Path.GetFileName(directory));
    }

    private static void DeleteStageTree(string directory)
    {
        foreach (string member in Directory.EnumerateFileSystemEntries(directory))
        {
            var attrs = File.GetAttributes(member);
            if ((attrs & FileAttributes.Directory) != 0)
            {
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(member, recursive: false);
                else
                    DeleteStageTree(member);
            }
            else File.Delete(member);
        }
        Directory.Delete(directory, recursive: false);
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
        if (Directory.EnumerateDirectories(BrokerSettings.Stage).Take(25).Count() >= 25)
            throw new IOException("Protected staging is full; an administrator must remove old packages");
        long expectedTotal = files.Sum(file => file.GetProperty("size").GetInt64());
        if (expectedTotal > new DriveInfo(Path.GetPathRoot(BrokerSettings.Stage)!).AvailableFreeSpace - 1024L*1024*1024)
            throw new IOException("Insufficient free space for a second, protected installer copy");
        // Unique folder per operation, inherited protected Program Files ACLs.
        string target = Path.Combine(BrokerSettings.Stage, id + "-" + digest + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        CopyingStages.TryAdd(target, 0);
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
            // Copy is finished (even if it failed). Remove temporary files;
            // if Windows has a handle open, the recovery sweep will retry.
            CopyingStages.TryRemove(target, out _);
            try { CleanupStage(Path.Combine(target, entry)); }
            catch (Exception ex) { BrokerSettings.Audit("stage-copy-cleanup-error " + ex.Message); }
            throw;
        }
        finally { CopyingStages.TryRemove(target, out _); }
    }
}
