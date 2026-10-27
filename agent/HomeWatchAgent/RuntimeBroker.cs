using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomeWatchAgent;

// v0.5.8: a dedicated, on-demand UAC-elevated runtime process; NOT the update service.
// No remote command endpoint, arbitrary arguments to launched apps, or SYSTEM-token launches.
// The broker independently rechecks the server's per-artifact approval and on-disk hashes.
internal static class RuntimeBroker
{
    internal static Process Start(string mode, int id, string? stagedEntry = null)
    {
        if (mode is not ("installer" or "game" or "stop") || id <= 0)
            throw new ArgumentException("Invalid approved runtime request");
        var psi = new ProcessStartInfo(Application.ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas", // Windows must explicitly authorise this elevated process.
            WorkingDirectory = AppContext.BaseDirectory
        };
        psi.ArgumentList.Add("--elevated-runtime");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (stagedEntry != null) psi.ArgumentList.Add(stagedEntry);
        return Process.Start(psi) ?? throw new IOException("Unable to open the Windows runtime broker");
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length < 3 || args.Length > 4 || args[0] != "--elevated-runtime" ||
                !int.TryParse(args[2], out int id) || id <= 0)
                throw new InvalidOperationException("Invalid runtime request");
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Administrator approval is required for this action");
            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HomeWatch", "config.json");
            var config = JsonSerializer.Deserialize<AgentConfig>(await File.ReadAllTextAsync(configPath))
                ?? throw new IOException("HomeWatch device configuration is unavailable");
            if (string.IsNullOrWhiteSpace(config.DeviceId) || string.IsNullOrWhiteSpace(config.DeviceToken) ||
                string.IsNullOrWhiteSpace(config.ServerUrl)) throw new IOException("HomeWatch device is not enrolled");
            using var http = new HttpClient { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.Add("X-Device-ID", config.DeviceId);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.DeviceToken);

            if (args[1] == "installer" && args.Length == 4)
            {
                using var permission = await GetAuthorisation(http, $"agent/elevation/installer/{id}");
                await VerifyInstallerAsync(permission.RootElement, args[3], id);
                return await StartAndWaitAsync(args[3]);
            }
            if ((args[1] == "game" || args[1] == "stop") && args.Length == 3)
            {
                using var permission = await GetAuthorisation(http, $"agent/elevation/launch/{id}");
                string path = permission.RootElement.GetProperty("file_path").GetString() ?? "";
                string hash = permission.RootElement.GetProperty("sha256").GetString() ?? "";
                string name = permission.RootElement.GetProperty("file_name").GetString() ?? "";
                long size = permission.RootElement.GetProperty("file_size").GetInt64();
                if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") ||
                    !string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid approved game path");
                await VerifyFileAsync(path, hash, size);
                if (args[1] == "stop") return StopExactGame(path);
                return await StartAndWaitAsync(path);
            }
            throw new InvalidOperationException("Unsupported runtime action");
        }
        catch (Exception ex)
        {
            MessageBox.Show("HomeWatch could not complete the administrator-approved action.\n\n" + ex.Message,
                "HomeWatch Runtime Broker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static async Task<JsonDocument> GetAuthorisation(HttpClient http, string endpoint)
    {
        using var response = await http.GetAsync(endpoint);
        if (!response.IsSuccessStatusCode) throw new UnauthorizedAccessException(
            "The parent has not authorised this elevated action or the HomeWatch server is unavailable (HTTP " + (int)response.StatusCode + ")");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task VerifyInstallerAsync(JsonElement root, string entryPath, int id)
    {
        string digest = root.GetProperty("installer_sha256").GetString() ?? "";
        string entry = root.GetProperty("package_entrypoint").GetString() ?? "";
        var manifest = root.GetProperty("manifest");
        if (!Regex.IsMatch(digest, "^[a-f0-9]{64}$") || !ValidRelative(entry))
            throw new IOException("Invalid approved installer manifest");
        string full = Path.GetFullPath(entryPath);
        var rootDir = Path.GetDirectoryName(full);
        // Find the package root from its exact, parent-authorised folder name.
        string packageFolder = id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + digest;
        var packagePath = rootDir;
        for (int i = 0; i < entry.Split('\\').Length - 1; i++)
            packagePath = Path.GetDirectoryName(packagePath ?? "");
        if (string.IsNullOrWhiteSpace(packagePath) ||
            !string.Equals(Path.GetFileName(packagePath), packageFolder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(packagePath)), "Packages", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(Path.Combine(packagePath, entry)).Equals(full, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Installer is not in its verified HomeWatch staging folder");
        if (File.GetAttributes(packagePath).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Staging folder must not be a reparse point");
        var files = manifest.GetProperty("files");
        int count = 0;
        long total = 0;
        foreach (var file in files.EnumerateArray())
        {
            string relative = file.GetProperty("path").GetString() ?? "";
            string hash = file.GetProperty("sha256").GetString() ?? "";
            long length = file.GetProperty("size").GetInt64();
            if (!ValidRelative(relative) || ++count > 10000 || length < 0 || total > long.MaxValue - length)
                throw new IOException("Invalid approved package file");
            total += length;
            var filePath = Path.GetFullPath(Path.Combine(packagePath, relative));
            if (!filePath.StartsWith(Path.GetFullPath(packagePath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("File escaped the approved package folder");
            // A user-writable installer cache cannot be elevated based only on an old hash.
            await VerifyFileAsync(filePath, hash, length);
        }
        if (count == 0 || !File.Exists(full)) throw new IOException("Installer is incomplete");
    }

    private static bool ValidRelative(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 700 && !value.Contains('/') &&
        !value.StartsWith('\\') && !value.Contains(':') &&
        value.Split('\\').All(p => p.Length > 0 && p is not ("." or ".."));

    private static async Task VerifyFileAsync(string path, string expectedHash, long expectedBytes)
    {
        if (!Regex.IsMatch(expectedHash, "^[a-fA-F0-9]{64}$") || !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Approved file is missing or unsafe");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedBytes) throw new IOException("Approved executable size changed");
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!digest.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Approved file hash changed; administrator launch refused");
    }

    private static async Task<int> StartAndWaitAsync(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? ""
        }) ?? throw new IOException("Windows could not start the approved executable");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static int StopExactGame(string path)
    {
        int stopped = 0;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            try
            {
                if (p.SessionId != Process.GetCurrentProcess().SessionId) continue;
                if (string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                { p.Kill(entireProcessTree: true); stopped++; }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return stopped > 0 ? 0 : 1;
    }
}
