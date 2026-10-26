using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// SECURITY BOUNDARY: no IPC commands are interpreted as programs, URLs, or paths.
// Update source and installed binary names are fixed by this elevated executable.
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
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HomeWatch", "0.5.2"));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Settings.State);
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
        if (remote <= installed)
        {
            UpdateStatus.Write("current", "Already on the newest available release", CurrentVersion());
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

    private static bool Trusted(string url, string tag)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Scheme == "https" && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && u.AbsolutePath.StartsWith("/Zeragonii/Homewatch/releases/download/" + tag + "/", StringComparison.OrdinalIgnoreCase)
            && !u.AbsolutePath.Contains("..", StringComparison.Ordinal);
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
