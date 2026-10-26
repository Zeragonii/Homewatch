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
                // Hash as bytes stream from the SMB share into the staging file.
                // A second full disk read is unnecessary for a newly copied file.
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    if (input.Length != file.Size) throw new IOException($"Source changed: {relative}");
                    var buffer = new byte[1024 * 1024];
                    long copied = 0;
                    int read;
                    var progressClock = Stopwatch.StartNew();
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        copied += read;
                        if (copied > file.Size) throw new IOException($"Unexpected file size: {relative}");
                        sha.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        // Windows Forms must not redraw the status for every 1 MiB block.
                        if (progressClock.ElapsedMilliseconds >= 200)
                        {
                            progress($"Copying {relative}… {(done + copied) * 100.0 / Math.Max(1, manifest.TotalBytes):F1}%");
                            progressClock.Restart();
                        }
                    }
                    if (copied != file.Size) throw new IOException($"Incomplete copy: {relative}");
                    if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(file.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"SHA-256 mismatch: {relative}. The source may have changed since approval.");
                    // The output stream is disposed before the file is renamed.
                    await output.FlushAsync(token);
                }
                File.Move(temp, destination, true);
                done += file.Size;
                progress($"Copied and verified {relative} — {done * 100.0 / Math.Max(1, manifest.TotalBytes):F1}%");
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
        // Existing cached files were checked when reused. Newly copied files
        // were verified while streaming and require no redundant second pass.

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

    // Find installation *roots*, not the immediate folder containing a game EXE.
    // A game may store the executable under several nested directories.
    internal static List<string> FindInstallRoots(HashSet<string> previous, string title, List<string> gameFolders)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in UninstallLocations().Except(previous, StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(folder)) roots.Add(Path.GetFullPath(folder).TrimEnd('\\'));
        var name = NormalizeGameTitle(title);
        if (name.Length < 3) return roots.ToList();
        foreach (var configured in gameFolders.Take(12))
        {
            try
            {
                if (!Directory.Exists(configured)) continue;
                foreach (var folder in Directory.EnumerateDirectories(configured).Take(200))
                {
                    var found = NormalizeGameTitle(Path.GetFileName(folder));
                    if (found.Length < 3) continue;
                    if (found.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(found, StringComparison.OrdinalIgnoreCase))
                        roots.Add(Path.GetFullPath(folder).TrimEnd('\\'));
                }
            }
            catch { /* Missing drive or inaccessible folder: skip it. */ }
        }
        return roots.OrderBy(x => x.Length).ToList();
    }

    internal static string? InferInstallRoot(string exe, HashSet<string> previous, string title, List<string> gameFolders)
    {
        var path = Path.GetFullPath(exe);
        return FindInstallRoots(previous, title, gameFolders).FirstOrDefault(root =>
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? FindApprovedGameExecutable(HashSet<string> previous, string title,
        List<string> gameFolders, string approvedRelative)
    {
        // The approved value came from the admin-reviewed catalogue but never
        // allow it to escape a guessed local installation folder.
        try { SafeRelative(approvedRelative); }
        catch (IOException) { return null; }
        if (!approvedRelative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in FindInstallRoots(previous, title, gameFolders))
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(root, approvedRelative));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
                matches.Add(full);
            }
            catch { }
        }
        // Don't guess between multiple installations or versions.
        return matches.Count == 1 ? matches.Single() : null;
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
