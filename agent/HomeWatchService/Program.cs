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
internal static class Settings
{
    public const string ServiceName = "HomeWatchUpdaterService";
    public const string Api = "https://api.github.com/repos/Zeragonii/Homewatch/releases/latest";
    public const string ReleasePrefix = "https://github.com/Zeragonii/Homewatch/releases/download/";
    public static readonly string State = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HomeWatch", ".updates");
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HomeWatch");
    public static readonly string[] Allowed = { "HomeWatchAgent.exe", "HomeWatchUpdater.exe", "HomeWatchService.exe" };
}

if (args.Length == 3 && args[0] == "--apply" && args[1] == "--service-update")
{
    // Only our service launches this; never accept an arbitrary source path.
    // The GUID folder lives inside an administrator-only ProgramData directory.
    await ApplyUpdate.Run(args[2]);
    return;
}
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = Settings.ServiceName);
builder.Services.AddHostedService<ReleaseWorker>();
await builder.Build().RunAsync();

internal sealed class ReleaseWorker(ILogger<ReleaseWorker> log) : BackgroundService
{
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(50) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HomeWatch", "0.3.8"));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Settings.State);
        var request = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HomeWatch", "check-update.request");
        var lastRequest = DateTime.MinValue;
        var nextCheck = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var requestTime = File.Exists(request) ? File.GetLastWriteTimeUtc(request) : DateTime.MinValue;
                if (DateTime.UtcNow >= nextCheck || requestTime > lastRequest)
                {
                    lastRequest = requestTime;
                    nextCheck = DateTime.UtcNow.AddMinutes(15);
                    await CheckAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Update check failed"); }
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync(Settings.Api, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var remote)) return;
        var installed = typeof(ReleaseWorker).Assembly.GetName().Version ?? new Version(0, 0);
        if (remote <= installed) return;
        string? zipUrl = null, shaUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == "HomeWatch-Agent-win-x64.zip") zipUrl = url;
            if (name == "HomeWatch-Agent-win-x64.sha256") shaUrl = url;
        }
        if (zipUrl is null || shaUrl is null || !Trusted(zipUrl, tag) || !Trusted(shaUrl, tag)) return;
        log.LogInformation("Verified release source contains candidate {Tag}", tag);
        var expected = (await Http.GetStringAsync(shaUrl, ct)).Trim().Split(' ')[0].Trim();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid release checksum");
        var archive = await Http.GetByteArrayAsync(zipUrl, ct);
        if (archive.Length > 100 * 1024 * 1024) throw new InvalidDataException("Archive too large");
        var actual = Convert.ToHexString(SHA256.HashData(archive));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Checksum mismatch");
        var stage = Path.Combine(Settings.State, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (!Settings.Allowed.Contains(entry.FullName, StringComparer.OrdinalIgnoreCase) ||
                    !names.Add(entry.FullName) || entry.Length > 50 * 1024 * 1024 ||
                    (entry.ExternalAttributes & 0xF0000000) == 0xA0000000)
                    throw new InvalidDataException("Unexpected release content");
                entry.ExtractToFile(Path.Combine(stage, entry.FullName));
            }
            if (names.Count != Settings.Allowed.Length) throw new InvalidDataException("Missing required binaries");
            // Helper is launched from protected Program Files, never the downloaded archive.
            var helper = Path.Combine(Settings.State, "apply-helper-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Path.Combine(Settings.InstallDir, "HomeWatchService.exe"), helper);
            Process.Start(new ProcessStartInfo(helper) {
                ArgumentList = { "--apply", "--service-update", Path.GetFileName(stage) },
                UseShellExecute = false, CreateNoWindow = true
            }) ?? throw new IOException("Could not launch update helper");
            log.LogInformation("Staged {Tag}; handing off to privileged helper", tag);
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
    public static async Task Run(string stageName)
    {
        if (!stageName.StartsWith("stage-", StringComparison.Ordinal) ||
            stageName.Length != 38 || !stageName[6..].All(Uri.IsHexDigit)) return;
        var dir = Path.Combine(Settings.State, stageName);
        if (!Directory.Exists(dir) || Settings.Allowed.Any(x => !File.Exists(Path.Combine(dir, x)))) return;
        // Ask each running per-user agent to restart itself after the files settle.
        // Agents can save state and launch their delayed re-starter under their own user.
        File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HomeWatch", "restart-agent.request"), DateTime.UtcNow.Ticks.ToString());
        await Task.Delay(7000);
        await ShellSc("stop", Settings.ServiceName);
        await Task.Delay(4500);
        foreach (var name in Settings.Allowed)
        {
            var src = Path.Combine(dir, name);
            var dst = Path.Combine(Settings.InstallDir, name);
            // Agent processes run in interactive user sessions; they will restart at next logon
            // if Windows still has their binaries open. We retry this file while updating.
            if (name == "HomeWatchAgent.exe")
                await KillRunningAgents();
            for (int i = 0; i < 15; i++)
            {
                try { File.Copy(src, dst, true); break; }
                catch (IOException) when (i < 14) { await Task.Delay(1000); }
            }
        }
        await ShellSc("start", Settings.ServiceName);
        // Cooperating interactive agents restart themselves after the update.
        // Sessions without an active agent will still start it at the next sign-in.
        try { Directory.Delete(dir, true); } catch { }
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
        if (p != null) await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }
}
