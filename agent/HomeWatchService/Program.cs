using System.Diagnostics;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// SECURITY BOUNDARY: no IPC commands are interpreted as programs, URLs, or paths.
// Update source and installed binary names are fixed by this elevated executable.
if (args.Length == 4 && args[0] == "--apply" && args[1] == "--broker-update")
{
    await BrokerUpdate.Run(args[2], args[3]);
    return;
}
if (args.Length == 4 && args[0] == "--apply" && args[1] == "--service-update")
{
    // Only our service launches this; never accept an arbitrary source path.
    // The GUID folder lives inside an administrator-only ProgramData directory.
    await ApplyUpdate.Run(args[2], args[3]);
    return;
}
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = Settings.ServiceName);
builder.Services.AddHostedService<ReleaseWorker>();
await builder.Build().RunAsync();

internal static class Settings
{
    public const string ServiceName = "HomeWatchUpdaterService";
    public const string BrokerServiceName = "HomeWatchInstallerBroker";
    public const string BrokerBinary = "HomeWatchInstallerBroker.exe";
    public const string BrokerAsset = "HomeWatchInstallerBroker-win-x64.exe";
    public const string BrokerChecksumAsset = "HomeWatchInstallerBroker-win-x64.sha256";
    public const string Api = "https://api.github.com/repos/Zeragonii/Homewatch/releases/latest";
    public const string ReleasePrefix = "https://github.com/Zeragonii/Homewatch/releases/download/";
    // Resolve the actual service installation, not a guessed Program Files location.
    // The temporary apply helper is copied into <install>\.updates, so for that
    // process we must resolve its parent directory as the installation directory.
    public static readonly string InstallDir = FindInstallDir();
    public static readonly string State = Path.Combine(InstallDir, ".updates");
    private static string FindInstallDir()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new IOException("Unable to determine HomeWatch updater executable path");
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))
            ?? throw new IOException("Unable to determine HomeWatch installation directory");
        if (Path.GetFileName(directory).Equals(".updates", StringComparison.OrdinalIgnoreCase))
            directory = Directory.GetParent(directory)?.FullName
                ?? throw new IOException("Unable to resolve HomeWatch installation from update helper");
        return directory;
    }
    public static readonly string[] Allowed = { "HomeWatchAgent.exe", "HomeWatchUpdater.exe", "HomeWatchService.exe" };
    public const long MaxArchiveBytes = 350L * 1024 * 1024;
    public const long MaxEntryBytes = 200L * 1024 * 1024;
    public static Version Canonical(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

// Machine-readable diagnostics are stored in an administrator-controlled directory.
// An ordinary user's agent can READ these but cannot forge a successful update.
internal static class UpdateStatus
{
    public static void Write(string phase, string message, string version = "")
    {
        try
        {
            Directory.CreateDirectory(Settings.State);
            var entry = new { phase, message = message.Length > 500 ? message[..500] : message,
                version, updated_utc = DateTime.UtcNow.ToString("O") };
            var json = JsonSerializer.Serialize(entry);
            var tmp = Path.Combine(Settings.State, "status-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path.Combine(Settings.State, "status.json"), true);
            File.AppendAllText(Path.Combine(Settings.State, "updater.log"),
                $"{DateTime.UtcNow:O} {phase} {version} {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
        }
        catch { /* Diagnostic failures never authorize or block an update. */ }
    }
}

internal sealed class ReleaseWorker(ILogger<ReleaseWorker> log) : BackgroundService
{
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HomeWatch", "0.6.3"));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Settings.State);
        BrokerUpdate.CleanupStaleArtifacts();
        var request = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HomeWatch", "check-update.request");
        var lastRequest = "";
        var nextCheck = DateTime.MinValue;
        UpdateStatus.Write("ready", "HomeWatch updater service running", CurrentVersion());
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var requestValue = File.Exists(request) ? File.ReadAllText(request).Trim() : "";
                if (DateTime.UtcNow >= nextCheck || (requestValue.Length > 0 && requestValue != lastRequest))
                {
                    lastRequest = requestValue;
                    nextCheck = DateTime.UtcNow.AddMinutes(15);
                    await CheckAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Update check failed");
                UpdateStatus.Write("failed", ex.Message, CurrentVersion());
            }
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        }
    }

    private static string CurrentVersion() => typeof(ReleaseWorker).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";

    private async Task CheckAsync(CancellationToken ct)
    {
        UpdateStatus.Write("checking", "Checking GitHub for a newer approved release", CurrentVersion());
        using var response = await Http.GetAsync(Settings.Api, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
        {
            UpdateStatus.Write("current", "No stable release available", CurrentVersion());
            return;
        }
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var remote))
            throw new InvalidDataException("GitHub release tag is not a valid version");
        var installed = typeof(ReleaseWorker).Assembly.GetName().Version ?? new Version(0, 0);
        if (Settings.Canonical(remote) <= Settings.Canonical(installed))
        {
            // Older clients require a 3-file archive for the first upgrade.
            // The 4th (signed) service binary is therefore a separate release
            // asset, installed here *after* the new updater service is running.
            if (Settings.Canonical(remote) == Settings.Canonical(installed))
                await CheckBrokerAsync(root, tag, remote, ct);
            else
                UpdateStatus.Write("current", "Already on a newer release", CurrentVersion());
            return;
        }
        string? zipUrl = null, shaUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == "HomeWatch-Agent-win-x64.zip") zipUrl = url;
            if (name == "HomeWatch-Agent-win-x64.sha256") shaUrl = url;
        }
        if (zipUrl is null || shaUrl is null || !Trusted(zipUrl, tag) || !Trusted(shaUrl, tag))
            throw new InvalidDataException($"Release {tag} is missing expected update assets");
        UpdateStatus.Write("downloading", $"Downloading {tag}", tag[1..]);
        var expected = (await Http.GetStringAsync(shaUrl, ct)).Trim().Split(' ')[0].Trim();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid release checksum");
        // Stream with an explicit limit: self-contained .NET builds can exceed 50MB
        // per executable. The old 50MB/100MB caps silently blocked releases.
        using var archiveResponse = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        archiveResponse.EnsureSuccessStatusCode();
        if (archiveResponse.Content.Headers.ContentLength > Settings.MaxArchiveBytes)
            throw new InvalidDataException("Update archive exceeds 350 MiB cap");
        await using var source = await archiveResponse.Content.ReadAsStreamAsync(ct);
        using var stream = new MemoryStream();
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (stream.Length + read > Settings.MaxArchiveBytes)
                throw new InvalidDataException("Update archive exceeds 350 MiB cap");
            stream.Write(buffer, 0, read);
        }
        var archive = stream.ToArray();
        var actual = Convert.ToHexString(SHA256.HashData(archive));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release archive SHA-256 mismatch");
        var stage = Path.Combine(Settings.State, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long uncompressedTotal = 0;
            foreach (var entry in zip.Entries)
            {
                uncompressedTotal += entry.Length;
                if (!Settings.Allowed.Contains(entry.FullName, StringComparer.OrdinalIgnoreCase) ||
                    !names.Add(entry.FullName) || entry.Length > Settings.MaxEntryBytes ||
                    uncompressedTotal > 450L * 1024 * 1024 ||
                    (entry.ExternalAttributes & 0xF0000000) == 0xA0000000)
                    throw new InvalidDataException("Unexpected or oversized release content");
                entry.ExtractToFile(Path.Combine(stage, entry.FullName));
            }
            if (names.Count != Settings.Allowed.Length)
                throw new InvalidDataException("Missing required update binaries");
            // Fail closed: the server's uploaded certificate is never an authority to change this pin.
            // Windows Authenticode chain/digest must be valid and every binary must have the SAME
            // locally-admin-approved public certificate before the helper can run.
            SignaturePolicy.VerifyAll(stage);
            // The currently installed SYSTEM service is copied to a protected helper
            // so it can stop/replace its own executable without a file lock.
            var helper = Path.Combine(Settings.State, "apply-helper-" + Guid.NewGuid().ToString("N") + ".exe");
            // Use the actual running service path; the installer may have been placed
            // in a custom directory or by an older installer under Program Files (x86).
            // Never accept a path outside the service's own protected install directory.
            var currentExecutable = Environment.ProcessPath;
            var expectedExecutable = Path.GetFullPath(Path.Combine(Settings.InstallDir, "HomeWatchService.exe"));
            if (string.IsNullOrWhiteSpace(currentExecutable) || !File.Exists(currentExecutable))
                throw new FileNotFoundException("The running HomeWatch service executable is missing. Run the latest HomeWatch installer as administrator to repair the service registration.", currentExecutable);
            if (!Path.GetFullPath(currentExecutable).Equals(expectedExecutable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"HomeWatch service is running from {currentExecutable}, but expected {expectedExecutable}. Run the latest HomeWatch installer as administrator to repair its service path.");
            File.Copy(currentExecutable, helper);
            UpdateStatus.Write("installing", $"Applying {tag}", tag[1..]);
            using var updateProcess = Process.Start(new ProcessStartInfo(helper) {
                ArgumentList = { "--apply", "--service-update", Path.GetFileName(stage), tag[1..] },
                UseShellExecute = false, CreateNoWindow = true
            });
            if (updateProcess is null) throw new IOException("Could not launch update helper");
            log.LogInformation("Staged {Tag}; handoff PID {ProcessId}", tag, updateProcess.Id);
        }
        catch { Directory.Delete(stage, true); throw; }
    }

    private async Task CheckBrokerAsync(JsonElement release, string tag, Version remote, CancellationToken ct)
    {
        // Only the dedicated SYSTEM update service can create these protected stages.
        // The already-installed broker service and its local identity are NEVER
        // recreated, re-enrolled or reconfigured by this update.
        string binary = Path.Combine(Settings.InstallDir, Settings.BrokerBinary);
        if (!File.Exists(binary))
            throw new FileNotFoundException("Installer broker is not installed on this PC. One initial administrator installation is required.", binary);
        string? current = FileVersionInfo.GetVersionInfo(binary).FileVersion;
        if (!Version.TryParse(current, out var currentVersion))
            throw new InvalidDataException("Cannot read the installed broker binary version");
        if (Settings.Canonical(currentVersion) >= Settings.Canonical(remote))
        {
            UpdateStatus.Write("current", "HomeWatch and installer broker are up to date", CurrentVersion());
            return;
        }

        string? download = null, checksum = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == Settings.BrokerAsset) download = url;
            if (name == Settings.BrokerChecksumAsset) checksum = url;
        }
        if (download is null || checksum is null || !Trusted(download, tag) || !Trusted(checksum, tag))
            throw new InvalidDataException($"Release {tag} does not provide the signed broker update assets");
        UpdateStatus.Write("broker-downloading", $"Fetching signed installer broker for {tag}", tag[1..]);
        var expected = (await Http.GetStringAsync(checksum, ct)).Trim().Split(' ')[0].Trim();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("Broker asset checksum is invalid");
        using var response = await Http.GetAsync(download, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > Settings.MaxEntryBytes)
            throw new InvalidDataException("Broker executable exceeds download limit");
        string stage = Path.Combine(Settings.State, "broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool handedOff = false;
        try
        {
            string staged = Path.Combine(stage, Settings.BrokerBinary);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(staged))
            {
                byte[] buffer = new byte[128 * 1024];
                int count;
                long length = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    length += count;
                    if (length > Settings.MaxEntryBytes)
                        throw new InvalidDataException("Broker executable exceeds download limit");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Broker executable SHA-256 mismatch");
            }
            // Crucial: SHA-256 supplied by the public release alone is not trusted.
            // The downloaded executable MUST chain to the admin-pinned publisher.
            SignaturePolicy.VerifyBroker(staged);
            string? suppliedVersion = FileVersionInfo.GetVersionInfo(staged).FileVersion;
            if (!Version.TryParse(suppliedVersion, out var packaged) ||
                Settings.Canonical(packaged) != Settings.Canonical(remote))
                throw new InvalidDataException("Signed broker file version does not match GitHub release");
            // Fail closed if the previous broker executable belongs to another service.
            await BrokerUpdate.VerifyServiceRegistration();
            if (BrokerUpdate.InstallationInProgress())
            {
                UpdateStatus.Write("deferred", "An installation is in progress or recently staged; broker update postponed", tag[1..]);
                return;
            }
            string helper = CreateApplyHelper();
            UpdateStatus.Write("broker-installing", "Updating signed installer broker (no manual installer required)", tag[1..]);
            using var p = Process.Start(new ProcessStartInfo(helper)
            {
                ArgumentList = { "--apply", "--broker-update", Path.GetFileName(stage), tag[1..] },
                UseShellExecute = false, CreateNoWindow = true
            });
            if (p is null) throw new IOException("Could not launch protected broker update helper");
            handedOff = true;
            log.LogInformation("Broker update helper PID={Pid} for version {Tag}", p.Id, tag);
        }
        finally
        {
            // The detached helper consumes its protected stage only after a
            // successful handoff. Do not retain half-downloaded or deferred files.
            if (!handedOff)
            {
                try { Directory.Delete(stage, true); } catch { }
            }
        }
    }

    private static string CreateApplyHelper()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Missing updater executable path");
        string expected = Path.GetFullPath(Path.Combine(Settings.InstallDir, "HomeWatchService.exe"));
        if (!Path.GetFullPath(executable).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Updater service is not running from its protected installation path");
        string helper = Path.Combine(Settings.State, "apply-helper-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(executable, helper);
        return helper;
    }

    private static bool Trusted(string url, string tag)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Scheme == "https" && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && u.AbsolutePath.StartsWith("/Zeragonii/Homewatch/releases/download/" + tag + "/", StringComparison.OrdinalIgnoreCase)
            && !u.AbsolutePath.Contains("..", StringComparison.Ordinal);
    }
}

// A standalone signed-asset path preserves old 3-executable updater ZIP compatibility.
// The broker has its own transactional stop/copy/start cycle and never changes
// the dedicated administrator account, encrypted secret, staging, or enrolment.
internal static class BrokerUpdate
{
    internal static void CleanupStaleArtifacts()
    {
        // These names are minted only by the updater inside the protected
        // .updates directory. Never clean rollback backups automatically:
        // they may be necessary if Windows could not restore the old service.
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-2);
            foreach (var directory in Directory.EnumerateDirectories(Settings.State))
            {
                try
                {
                    string name = Path.GetFileName(directory);
                    if (!Regex.IsMatch(name, @"^broker-[a-fA-F0-9]{32}$") ||
                        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                        Directory.GetCreationTimeUtc(directory) >= cutoff) continue;
                    Directory.Delete(directory, true);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            foreach (var path in Directory.EnumerateFiles(Settings.State))
            {
                try
                {
                    string name = Path.GetFileName(path);
                    if (!Regex.IsMatch(name, @"^apply-helper-[a-fA-F0-9]{32}\.exe$") ||
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                        File.GetCreationTimeUtc(path) >= cutoff) continue;
                    File.Delete(path); // fails safely if the helper is still executing
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static async Task VerifyServiceRegistration()
    {
        var config = await RunSc("qc", Settings.BrokerServiceName);
        if (config.ExitCode != 0)
            throw new IOException("HomeWatchInstallerBroker service not found; install HomeWatch once as administrator to register it");
        string? registered = config.Output.Split('\n')
            .Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase));
        if (registered is null || !registered.Contains(':'))
            throw new IOException("Cannot verify installer broker service's executable path");
        registered = registered[(registered.IndexOf(':') + 1)..].Trim().Trim('"');
        string target = Path.GetFullPath(Path.Combine(Settings.InstallDir, Settings.BrokerBinary));
        if (!Path.GetFullPath(registered).Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Broker service points outside the verified HomeWatch install directory; repair using the signed installer");
    }

    internal static bool InstallationInProgress()
    {
        // Do not interrupt broker staging or a wizard that runs from its protected
        // package. Keep this conservative, especially for existing v0.6.2 brokers.
        string root = Path.Combine(Settings.InstallDir, ".installer-broker", "staged");
        if (!Directory.Exists(root)) return false;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return true;
                if (Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddMinutes(-10))
                    return true;
            }
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        string? path = proc.MainModule?.FileName;
                        if (path is not null && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        return false;
    }

    internal static async Task Run(string stageName, string version)
    {
        if (!Regex.IsMatch(stageName, @"^broker-[a-f0-9]{32}$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(version, out var expectedVersion)) return;
        string stage = Path.Combine(Settings.State, stageName);
        string source = Path.Combine(stage, Settings.BrokerBinary);
        if (!File.Exists(source)) return;
        SignaturePolicy.VerifyBroker(source);
        var signedVersion = FileVersionInfo.GetVersionInfo(source).FileVersion;
        if (!Version.TryParse(signedVersion, out var actualVersion) ||
            Settings.Canonical(expectedVersion) != Settings.Canonical(actualVersion))
            throw new InvalidDataException("Broker helper refused inconsistent signed version");
        await VerifyServiceRegistration();
        if (InstallationInProgress())
        {
            UpdateStatus.Write("deferred", "Broker update deferred because an installation is active", version);
            return;
        }
        string installed = Path.Combine(Settings.InstallDir, Settings.BrokerBinary);
        string backup = Path.Combine(Settings.State, "broker-backup-" + Guid.NewGuid().ToString("N") + ".exe");
        bool wasRunning = false;
        bool modified = false;
        try
        {
            int state = await QueryState();
            wasRunning = state != 1;
            if (wasRunning)
            {
                var stop = await RunSc("stop", Settings.BrokerServiceName);
                if (stop.ExitCode != 0 && await QueryState() != 1)
                    throw new IOException("Could not request broker service shutdown: " + stop.Output);
                await WaitState(1);
            }
            // NTFS atomic replacement: never leave a half-written privileged
            // service EXE behind after a crash or interrupted copy. Windows
            // creates the rollback backup as part of the same filesystem operation.
            File.Replace(source, installed, backup, ignoreMetadataErrors: false);
            modified = true;
            SignaturePolicy.VerifyBroker(installed);
            if (wasRunning)
            {
                var start = await RunSc("start", Settings.BrokerServiceName);
                if (start.ExitCode != 0 && await QueryState() != 4)
                    throw new IOException("Could not start updated broker service: " + start.Output);
                await WaitState(4);
                // SCM reporting RUNNING is not sufficient if the new binary
                // crashes immediately after service start.
                await Task.Delay(2500);
                if (await QueryState() != 4)
                    throw new IOException("Updated installer broker exited shortly after startup");
            }
            UpdateStatus.Write("installed", "HomeWatch installer broker updated remotely; existing enrolment and identity preserved", version);
            try { File.Delete(backup); } catch { }
            try { Directory.Delete(stage, true); } catch { }
        }
        catch (Exception ex)
        {
            UpdateStatus.Write("failed", "Broker update failed; rolling back: " + ex.Message, version);
            if (modified)
            {
                try
                {
                    if (await QueryState() != 1)
                    {
                        await RunSc("stop", Settings.BrokerServiceName);
                        await WaitState(1);
                    }
                    File.Replace(backup, installed, null, ignoreMetadataErrors: false);
                    if (wasRunning)
                    {
                        var restored = await RunSc("start", Settings.BrokerServiceName);
                        if (restored.ExitCode != 0 && await QueryState() != 4)
                            throw new IOException("Could not restart previous broker");
                        await WaitState(4);
                    }
                    UpdateStatus.Write("failed", "Previous broker restored after failed update", version);
                    try { File.Delete(backup); } catch { }
                }
                catch (Exception restore)
                {
                    UpdateStatus.Write("failed", "CRITICAL: broker rollback failed: " + restore.Message + "; backup retained at " + backup, version);
                }
            }
            throw;
        }
    }

    private static async Task WaitState(int expected)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (await QueryState() == expected) return;
            await Task.Delay(600);
        }
        throw new IOException("Timed out waiting for installer broker service state " + expected);
    }

    private static async Task<int> QueryState()
    {
        var result = await RunSc("query", Settings.BrokerServiceName);
        if (result.ExitCode != 0) throw new IOException("Cannot query installer broker service: " + result.Output);
        var match = Regex.Match(result.Output, @"(?m)^\s*STATE\s*:\s*(\d+)");
        if (!match.Success) throw new IOException("Unrecognised service state from sc.exe");
        return int.Parse(match.Groups[1].Value);
    }

    private static async Task<(int ExitCode, string Output)> RunSc(string verb, string name)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe")
        {
            ArgumentList = { verb, name },
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new IOException("Could not start sc.exe");
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return (process.ExitCode, stdout + "\n" + stderr);
    }
}

internal static class ApplyUpdate
{
    public static async Task Run(string stageName, string targetVersion)
    {
        if (!stageName.StartsWith("stage-", StringComparison.Ordinal) ||
            stageName.Length != 38 || !stageName[6..].All(Uri.IsHexDigit) ||
            !Version.TryParse(targetVersion, out _)) return;
        var dir = Path.Combine(Settings.State, stageName);
        if (!Directory.Exists(dir) || Settings.Allowed.Any(x => !File.Exists(Path.Combine(dir, x)))) return;
        // Verify once more in the independent apply process immediately before replacement.
        SignaturePolicy.VerifyAll(dir);
        var backup = Path.Combine(Settings.State, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        try
        {
            // Preserve the original binaries for rollback before modifying anything.
            foreach (var name in Settings.Allowed)
                File.Copy(Path.Combine(Settings.InstallDir, name), Path.Combine(backup, name), true);
            File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "HomeWatch", "restart-agent.request"), DateTime.UtcNow.Ticks.ToString());
            await Task.Delay(7000);
            await ShellSc("stop", Settings.ServiceName);
            await Task.Delay(3500);
            await KillRunningAgents();
            foreach (var name in Settings.Allowed)
            {
                var src = Path.Combine(dir, name);
                var dst = Path.Combine(Settings.InstallDir, name);
                await CopyWithRetry(src, dst);
            }
            await ShellSc("start", Settings.ServiceName);
            UpdateStatus.Write("installed", "Update installed; agent will restart in signed-in session", targetVersion);
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(backup, true); } catch { }
        }
        catch (Exception ex)
        {
            UpdateStatus.Write("failed", "Installation failed; attempting rollback: " + ex.Message, targetVersion);
            foreach (var name in Settings.Allowed)
            {
                try { await CopyWithRetry(Path.Combine(backup, name), Path.Combine(Settings.InstallDir, name)); }
                catch (Exception restoreEx) { UpdateStatus.Write("failed", "Rollback also failed: " + restoreEx.Message, targetVersion); }
            }
            try { await ShellSc("start", Settings.ServiceName); } catch { }
            throw;
        }
    }

    private static async Task CopyWithRetry(string src, string dest)
    {
        for (var i = 0; i < 20; i++)
        {
            try { File.Copy(src, dest, true); return; }
            catch (IOException) when (i < 19) { await Task.Delay(800); }
        }
    }

    private static async Task KillRunningAgents()
    {
        foreach (var proc in Process.GetProcessesByName("HomeWatchAgent"))
        {
            try { proc.Kill(); await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
            finally { proc.Dispose(); }
        }
    }
    private static async Task ShellSc(string verb, string name)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe") { ArgumentList = { verb, name },
            UseShellExecute = false, CreateNoWindow = true });
        if (p is null) throw new IOException("Could not start sc.exe");
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        if (p.ExitCode != 0) throw new IOException($"sc.exe {verb} failed ({p.ExitCode})");
    }
}
