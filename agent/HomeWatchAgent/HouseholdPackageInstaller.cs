using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HomeWatchAgent;

// Ordinary-user interactive installation. No privilege elevation, no arbitrary
// code execution through the SYSTEM HomeWatch updater service.
internal static class HouseholdPackageInstaller
{
    private const long MaximumPackageSize = 300L * 1024 * 1024 * 1024;

    internal static async Task<string> InstallAsync(PortalCatalogueItem item, Action<string> progress,
        CancellationToken token)
    {
        if (item.Manifest is null) throw new IOException("Missing approved package manifest");
        var manifest = item.Manifest;
        if (manifest.Files.Count == 0 || manifest.Files.Count > 4000 || manifest.TotalBytes > MaximumPackageSize)
            throw new IOException("Package file count or size exceeds the supported limit");
        if (!item.PackageRoot.StartsWith(@"\\", StringComparison.Ordinal) || item.PackageRoot.Length < 5)
            throw new IOException("The parent must provide an approved SMB package share");
        var entrypoint = SafeRelative(manifest.Entrypoint);
        if (!entrypoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The package entrypoint must be a setup EXE");
        if (!manifest.Files.Any(x => string.Equals(x.Path, entrypoint, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Approved installer is missing from the manifest");
        if (!Regex.IsMatch(item.InstallerSha256, "^[a-f0-9]{64}$"))
            throw new IOException("Invalid approved package fingerprint");
        var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HomeWatch", "Packages", item.Id.ToString() + "-" + item.InstallerSha256);
        var drive = new DriveInfo(Path.GetPathRoot(stage)!);
        if (drive.AvailableFreeSpace < manifest.TotalBytes + 1024L * 1024 * 1024)
            throw new IOException("There isn't enough free disk space to stage this installer (1 GB reserve required)");
        Directory.CreateDirectory(stage);
        var normalizedRoot = item.PackageRoot.TrimEnd('\\');
        long done = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var relative = SafeRelative(file.Path);
            if (!seen.Add(relative) || !Regex.IsMatch(file.Sha256, "^[a-f0-9]{64}$") || file.Size < 0)
                throw new IOException("Invalid or duplicated manifest file");
            var source = normalizedRoot + "\\" + relative;
            var destination = Path.Combine(stage, relative);
            // Directory containment is checked even if a future server implementation
            // accidentally emits a path that Windows normalises differently.
            if (!Path.GetFullPath(destination).StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid staging path");
            if (File.Exists(destination) && new FileInfo(destination).Length == file.Size &&
                await MatchesAsync(destination, file.Sha256, token))
            {
                done += file.Size;
                progress($"Package cache checked: {done * 100.0 / Math.Max(1, manifest.TotalBytes):F1}%");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Staged file is a reparse point");
            var temp = destination + ".download";
            try
            {
                progress($"Copying {relative}… {done * 100.0 / Math.Max(1, manifest.TotalBytes):F1}%");
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                {
                    if (input.Length != file.Size) throw new IOException($"Source changed: {relative}");
                    var buffer = new byte[1024 * 1024];
                    long copied = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        copied += read;
                        if (copied > file.Size) throw new IOException($"Unexpected file size: {relative}");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        progress($"Copying {relative}… {(done + copied) * 100.0 / Math.Max(1, manifest.TotalBytes):F1}%");
                    }
                    if (copied != file.Size) throw new IOException($"Incomplete copy: {relative}");
                }
                progress($"Verifying {relative}…");
                if (!await MatchesAsync(temp, file.Sha256, token))
                    throw new IOException($"SHA-256 mismatch: {relative}. The source may have changed since approval.");
                File.Move(temp, destination, true);
                done += file.Size;
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
        // All copies are individually hashed. Verify them once more before execution.
        progress("Verifying the complete package…");
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(stage, SafeRelative(file.Path));
            if (!await MatchesAsync(target, file.Sha256, token))
                throw new IOException($"Package integrity check failed: {file.Path}");
        }
        return Path.Combine(stage, entrypoint);
    }

    internal static string SafeRelative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 700 || value.Contains('/') ||
            value.StartsWith('\\') || value.Contains(':')) throw new IOException("Invalid package member path");
        var parts = value.Split('\\');
        if (parts.Any(p => string.IsNullOrEmpty(p) || p is "." or ".." ||
            p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new IOException("Invalid package member path");
        return value;
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken token)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, token)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static List<string> FindInstalledGames(HashSet<string> previous, string title, List<string> gameFolders)
    {
        var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in UninstallLocations().Except(previous, StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(path))
                foreach (var exe in SafeEnumerateExes(path, 60)) list.Add(exe);
        // Well-known game locations can be discovered; never scan entire drives.
        var normTitle = NormalizeGameTitle(title);
        foreach (var root in gameFolders.Take(12))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root).Take(200))
                {
                    var gameDir = NormalizeGameTitle(Path.GetFileName(dir));
                    if (!gameDir.Contains(normTitle, StringComparison.OrdinalIgnoreCase) &&
                        !normTitle.Contains(gameDir, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var exe in SafeEnumerateExes(dir, 100)) list.Add(exe);
                }
            }
            catch { }
        }
        return list.OrderBy(x => x.Length).Take(25).ToList();
    }

    private static string NormalizeGameTitle(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static HashSet<string> UninstallLocations()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, view)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames().Take(2000))
                {
                    using var entry = key.OpenSubKey(name);
                    var path = entry?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                        !path.StartsWith(@"\\") && Directory.Exists(path)) found.Add(Path.GetFullPath(path));
                }
            }
            catch { }
        }
        return found;
    }

    private static IEnumerable<string> SafeEnumerateExes(string directory, int limit)
    {
        try { return Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).Take(limit).ToArray(); }
        catch { return Array.Empty<string>(); }
    }
}
