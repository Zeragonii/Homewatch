using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HomeWatchAgent;

// Ready-to-play folders are installed by the signed-in STANDARD user. This
// component does not communicate with, or grant commands to, the SYSTEM broker.
internal static class PortableGameDeployment
{
    private const long MaxBytes = 300L * 1024 * 1024 * 1024;
    private const int MaxPortableFiles = 100_000;
    private const long FreeSpaceReserve = 1024L * 1024 * 1024;

    internal static async Task<string> DeployAsync(PortalCatalogueItem item, Action<string> progress,
        CancellationToken token)
    {
        if (item.DeploymentType != "portable" || item.Manifest is null)
            throw new IOException("A published portable-game manifest is required");
        var manifest = item.Manifest;
        if (manifest.Files.Count is < 1 or > MaxPortableFiles || manifest.TotalBytes is < 0 or > MaxBytes ||
            !Regex.IsMatch(item.InstallerSha256, "^[a-f0-9]{64}$") ||
            !item.PackageRoot.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Invalid published game package");
        var entrypoint = HouseholdPackageInstaller.SafeRelative(manifest.Entrypoint);
        if (!entrypoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !manifest.Files.Any(f => f.Path.Equals(entrypoint, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The game EXE is absent from the approved manifest");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declared = 0;
        foreach (var f in manifest.Files)
        {
            HouseholdPackageInstaller.SafeRelative(f.Path);
            if (!seen.Add(f.Path) || f.Size < 0 || !Regex.IsMatch(f.Sha256, "^[a-f0-9]{64}$"))
                throw new IOException("Invalid or ambiguous game manifest");
            declared = checked(declared + f.Size);
        }
        if (declared != manifest.TotalBytes) throw new IOException("Published package size is inconsistent");

        var (library, final) = ChooseDestination(item);
        var stagingParent = Path.Combine(library, ".homewatch-staging");
        var stage = Path.Combine(stagingParent, item.Id + "-" + item.InstallerSha256);
        EnsureSafePath(library, stagingParent);
        if (!Directory.Exists(stage) && new DriveInfo(Path.GetPathRoot(library)!).AvailableFreeSpace <
            manifest.TotalBytes + FreeSpaceReserve)
            throw new IOException("Insufficient free disk space for this game (1 GB reserve required)");
        Directory.CreateDirectory(stagingParent);
        EnsureSafePath(library, stagingParent);
        Directory.CreateDirectory(stage);
        EnsureSafePath(library, stage);
        try { File.SetAttributes(stagingParent, File.GetAttributes(stagingParent) | FileAttributes.Hidden); }
        catch { /* Hidden attribute is cosmetic, verification is mandatory. */ }

        // If reporting failed after a successful promotion, reuse the fully
        // verified final directory instead of downloading it again.
        if (Directory.Exists(final) && await MatchesManifestAsync(final, manifest, token))
        {
            progress("Existing game verified; resuming registration…");
            return Path.Combine(final, entrypoint);
        }

        if (manifest.Transport == "zip-chunks-v1")
        {
            await DeployChunksAsync(item, manifest, stage, entrypoint, progress, token);
        }
        else if (string.IsNullOrEmpty(manifest.Transport))
        {
        // Compatibility: legacy portable packages published before v0.6.5.
        long done = 0;
        foreach (var f in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var relative = HouseholdPackageInstaller.SafeRelative(f.Path);
            var target = Path.GetFullPath(Path.Combine(stage, relative));
            AssertInside(stage, target);
            var parent = Path.GetDirectoryName(target)!;
            EnsureSafePath(stage, parent);
            Directory.CreateDirectory(parent);
            EnsureSafePath(stage, parent);
            EnsureSafePath(stage, target);
            if (File.Exists(target) && new FileInfo(target).Length == f.Size &&
                await MatchesAsync(target, f.Sha256, token))
            {
                var leftover = target + ".download";
                if (File.Exists(leftover))
                {
                    EnsureSafePath(stage, leftover);
                    File.Delete(leftover);
                }
                done += f.Size;
                progress($"Resumed {done * 100.0 / Math.Max(1, manifest.TotalBytes):F1}% from verified files");
                continue;
            }
            var part = target + ".download";
            EnsureSafePath(stage, part);
            try
            {
                var source = item.PackageRoot.TrimEnd('\\') + "\\" + relative;
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    if (input.Length != f.Size) throw new IOException("NAS package changed: " + relative);
                    var buffer = new byte[1024 * 1024];
                    long copied = 0;
                    var clock = Stopwatch.StartNew();
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        copied += read;
                        if (copied > f.Size) throw new IOException("Unexpected NAS file size: " + relative);
                        sha.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        if (clock.ElapsedMilliseconds > 250)
                        {
                            progress($"Copying {relative} — {(done + copied) * 100.0 / Math.Max(1, manifest.TotalBytes):F1}%");
                            clock.Restart();
                        }
                    }
                    if (copied != f.Size || !Convert.ToHexString(sha.GetHashAndReset()).Equals(f.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                        throw new IOException("SHA-256 mismatch: " + relative);
                    await output.FlushAsync(token);
                }
                File.Move(part, target, true); // same-volume metadata operation
                done += f.Size;
                progress($"Verified {relative} — {done * 100.0 / Math.Max(1, manifest.TotalBytes):F1}%");
            }
            finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
        }
        }
        else throw new IOException("Unsupported portable game distribution format");
        token.ThrowIfCancellationRequested();
        // Never promote unexpected content, partial downloads or directory links.
        AssertExactContents(stage, manifest);
        var exe = Path.Combine(stage, entrypoint);
        if (!File.Exists(exe) || !await MatchesAsync(exe,
                manifest.Files.First(f => f.Path.Equals(entrypoint, StringComparison.OrdinalIgnoreCase)).Sha256, token))
            throw new IOException("The approved game executable hash does not match");
        // Check size and file count without a second full read of all game assets.
        foreach (var f in manifest.Files)
        {
            var path = Path.Combine(stage, HouseholdPackageInstaller.SafeRelative(f.Path));
            if (!File.Exists(path) || new FileInfo(path).Length != f.Size)
                throw new IOException("Game staging is incomplete: " + f.Path);
        }

        // Replacing a game keeps the previous COMPLETE directory as a separate
        // rollback backup. We never overwrite saves/mods in place.
        // A matching manifest above is enough for recovery after reporting
        // fails, but never move a DIFFERENT, unregistered folder out of the way.
        if (Directory.Exists(final) &&
            !string.Equals(item.Installation?.InstallRoot, final, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A different folder already occupies this game path. HomeWatch will not overwrite or relocate it.");
        string? backup = null;
        if (Directory.Exists(final))
        {
            EnsureSafeTree(final);
            var backupParent = Path.Combine(library, ".homewatch-backups");
            EnsureSafePath(library, backupParent);
            Directory.CreateDirectory(backupParent);
            EnsureSafePath(library, backupParent);
            backup = Path.Combine(backupParent, item.Id + "-" +
                DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            Directory.Move(final, backup);
        }
        try { Directory.Move(stage, final); }
        catch
        {
            if (backup != null && !Directory.Exists(final) && Directory.Exists(backup))
                Directory.Move(backup, final);
            throw;
        }
        progress("Verified game promoted into the library. Ready for launch approval.");
        return Path.Combine(final, entrypoint);
    }

    private static async Task DeployChunksAsync(PortalCatalogueItem item, PackageManifest manifest,
        string stage, string entrypoint, Action<string> progress, CancellationToken token)
    {
        if (manifest.Chunks.Count is < 1 or > 1024)
            throw new IOException("Invalid published ZIP chunk count");
        // Do not accept arbitrary network archive roots supplied by a client:
        // the server must publish packages under this share's fixed cache path.
        var uncParts = item.PackageRoot.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (uncParts.Length < 2)
            throw new IOException("Invalid game source share");
        var expectedArchiveRoot = @"\\" + uncParts[0] + "\\" + uncParts[1] +
            @"\.homewatch-packages\" + item.InstallerSha256;
        if (!manifest.ArchiveRoot.Equals(expectedArchiveRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("ZIP archive root does not match the published NAS share and digest");

        int nextFile = 0;
        long expectedTotal = 0;
        var started = Stopwatch.StartNew();
        long completedBytes = 0;
        foreach (var chunk in manifest.Chunks)
        {
            token.ThrowIfCancellationRequested();
            if (!Regex.IsMatch(chunk.Name, @"^part-[0-9]{4}\.zip$", RegexOptions.IgnoreCase) ||
                chunk.FileStart != nextFile || chunk.FileCount < 1 ||
                nextFile + chunk.FileCount > manifest.Files.Count || chunk.UncompressedBytes < 0)
                throw new IOException("Invalid portable ZIP chunk manifest");
            var files = manifest.Files.GetRange(nextFile, chunk.FileCount);
            var declared = files.Sum(f => f.Size);
            if (declared != chunk.UncompressedBytes)
                throw new IOException("ZIP chunk manifest byte count mismatch");
            expectedTotal = checked(expectedTotal + declared);
            nextFile += chunk.FileCount;

            bool present = true;
            foreach (var file in files)
            {
                var target = Path.Combine(stage, HouseholdPackageInstaller.SafeRelative(file.Path));
                EnsureSafePath(stage, Path.GetDirectoryName(target)!);
                if (!File.Exists(target) || new FileInfo(target).Length != file.Size ||
                    File.Exists(target + ".download") ||
                    (file.Path.Equals(entrypoint, StringComparison.OrdinalIgnoreCase) &&
                     !await MatchesAsync(target, file.Sha256, token)))
                {
                    present = false;
                    break;
                }
            }
            if (present)
            {
                completedBytes += declared;
                progress($"Resumed {completedBytes * 100.0 / Math.Max(1, manifest.TotalBytes):F1}% from completed ZIP chunks");
                continue;
            }
            string source = manifest.ArchiveRoot + "\\" + chunk.Name;
            await using var remote = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            using var archive = new ZipArchive(remote, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count != files.Count)
                throw new IOException("ZIP chunk has missing or additional files: " + chunk.Name);
            long chunkCopied = 0;
            for (int i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var file = files[i];
                var relative = HouseholdPackageInstaller.SafeRelative(file.Path);
                var entry = archive.Entries[i];
                if (!entry.FullName.Replace('/', '\\').Equals(relative, StringComparison.OrdinalIgnoreCase) ||
                    entry.Length != file.Size || entry.CompressedLength != entry.Length)
                    throw new IOException("ZIP entry does not match the published uncompressed manifest: " + file.Path);
                var target = Path.GetFullPath(Path.Combine(stage, relative));
                AssertInside(stage, target);
                var parent = Path.GetDirectoryName(target)!;
                EnsureSafePath(stage, parent);
                Directory.CreateDirectory(parent);
                EnsureSafePath(stage, parent);
                EnsureSafePath(stage, target);
                var part = target + ".download";
                EnsureSafePath(stage, part);
                try
                {
                    bool isExecutable = relative.Equals(entrypoint, StringComparison.OrdinalIgnoreCase);
                    using var exeHash = isExecutable ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                    long copied = 0;
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write,
                        FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        // Stream from the SMB ZIP directly into final-drive staging:
                        // one SSD write and no local copy of the ZIP.
                        var buffer = new byte[1024 * 1024];
                        int read;
                        long lastReport = 0;
                        while ((read = await input.ReadAsync(buffer, token)) != 0)
                        {
                            copied += read;
                            if (copied > file.Size) throw new IOException("Oversized ZIP entry: " + relative);
                            exeHash?.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), token);
                            if (started.ElapsedMilliseconds - lastReport > 600)
                            {
                                var current = completedBytes + chunkCopied + copied;
                                var mbps = current / 1048576.0 / Math.Max(0.001, started.Elapsed.TotalSeconds);
                                progress($"Extracting ZIP chunks — {current * 100.0 / Math.Max(1, manifest.TotalBytes):F1}% ({mbps:F1} MiB/s)");
                                lastReport = started.ElapsedMilliseconds;
                            }
                        }
                        await output.FlushAsync(token);
                    }
                    if (copied != file.Size)
                        throw new IOException("Incomplete ZIP entry: " + relative);
                    if (isExecutable &&
                        !Convert.ToHexString(exeHash!.GetHashAndReset()).Equals(file.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Executable SHA-256 mismatch in ZIP: " + relative);
                    chunkCopied += copied;
                    File.Move(part, target, true);
                }
                finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
            }
            completedBytes += declared;
            var seconds = Math.Max(0.001, started.Elapsed.TotalSeconds);
            progress($"ZIP chunk {manifest.Chunks.IndexOf(chunk) + 1}/{manifest.Chunks.Count} — " +
                $"{completedBytes * 100.0 / Math.Max(1, manifest.TotalBytes):F1}% " +
                $"({completedBytes / 1048576.0 / seconds:F1} MiB/s)");
        }
        if (nextFile != manifest.Files.Count || expectedTotal != manifest.TotalBytes)
            throw new IOException("Incomplete ZIP manifest");
    }

    private static (string library, string final) ChooseDestination(PortalCatalogueItem item)
    {
        var roots = item.GameScanRoots.Take(12).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var installedRoot = item.Installation?.InstallRoot;
        if (!string.IsNullOrWhiteSpace(installedRoot))
        {
            var existing = Path.GetFullPath(installedRoot);
            var library = Path.GetDirectoryName(existing)!;
            if (roots.Any(x => IsAllowedRoot(x, library)) && Directory.Exists(library))
            {
                EnsureSafePath(library, existing);
                if (new DriveInfo(Path.GetPathRoot(library)!).AvailableFreeSpace < FreeSpaceReserve)
                    throw new IOException("Not enough free space to resume the game copy (1 GB reserve)");
                return (library, existing);
            }
            throw new IOException("The existing installation is outside the approved game folders");
        }
        var name = Regex.Replace(item.Name, "[^A-Za-z0-9 _.-]", "_").Trim(' ', '.');
        if (name.Length > 65) name = name[..65];
        if (string.IsNullOrEmpty(name)) name = "Game";
        foreach (var configured in roots)
        {
            try
            {
                if (!Path.IsPathFullyQualified(configured) || configured.StartsWith(@"\\") ||
                    !Regex.IsMatch(configured, @"^[A-Za-z]:\\")) continue;
                var root = Path.GetFullPath(configured).TrimEnd('\\');
                if (Path.GetPathRoot(root)!.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.Exists(root)) continue; // Don't create system folders implicitly
                EnsureSafePath(root, root);
                if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < FreeSpaceReserve) continue;
                var test = Path.Combine(root, ".hw-write-test-" + Guid.NewGuid().ToString("N"));
                try { using var stream = new FileStream(test, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                finally { if (File.Exists(test)) File.Delete(test); }
                return (root, Path.Combine(root, name + " [HW-" + item.Id + "]"));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            catch (ArgumentException) { }
        }
        throw new IOException("No writable, sufficiently large game folder is available. Ask your parent to configure C:\\Games or D:\\Games permissions.");
    }

    private static bool IsAllowedRoot(string configured, string actual)
    {
        try { return !configured.StartsWith(@"\\") && Path.IsPathFullyQualified(configured) &&
            Path.GetFullPath(configured).TrimEnd('\\').Equals(actual.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void AssertInside(string root, string path)
    {
        var basis = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (!Path.GetFullPath(path).StartsWith(basis, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Package path escapes the staging directory");
    }

    private static void EnsureSafePath(string root, string path)
    {
        var cursor = new DirectoryInfo(Path.GetFullPath(path));
        var limit = Path.GetFullPath(root).TrimEnd('\\');
        while (cursor != null)
        {
            if (File.Exists(cursor.FullName) || Directory.Exists(cursor.FullName))
                if ((File.GetAttributes(cursor.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Filesystem link is not allowed in game staging");
            if (cursor.FullName.TrimEnd('\\').Equals(limit, StringComparison.OrdinalIgnoreCase)) return;
            cursor = cursor.Parent;
        }
        throw new IOException("Path escapes the permitted game folder");
    }

    private static void EnsureSafeTree(string root)
    {
        var todo = new Stack<string>(); todo.Push(root);
        int seen = 0;
        while (todo.Count != 0)
        {
            var folder = todo.Pop();
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Game folder contains a directory link");
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++seen > 100000 || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Unsafe or oversized game directory tree");
                if (Directory.Exists(entry)) todo.Push(entry);
            }
        }
    }

    private static void AssertExactContents(string root, PackageManifest manifest)
    {
        var files = new HashSet<string>(manifest.Files.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        var todo = new Stack<string>(); todo.Push(root);
        int count = 0;
        while (todo.Count > 0)
        {
            var folder = todo.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A filesystem link was introduced into staging");
                if (Directory.Exists(entry)) todo.Push(entry);
                else
                {
                    count++;
                    var rel = Path.GetRelativePath(root, entry).Replace('/', '\\');
                    if (!files.Contains(rel)) throw new IOException("Unexpected file inside game staging: " + rel);
                }
            }
        }
        if (count != files.Count) throw new IOException("Game staging is missing published files");
    }

    private static async Task<bool> MatchesManifestAsync(string root, PackageManifest manifest, CancellationToken token)
    {
        try
        {
            EnsureSafeTree(root);
            AssertExactContents(root, manifest);
            foreach (var f in manifest.Files)
            {
                var target = Path.Combine(root, HouseholdPackageInstaller.SafeRelative(f.Path));
                if (!File.Exists(target) || new FileInfo(target).Length != f.Size ||
                    (f.Path.Equals(manifest.Entrypoint, StringComparison.OrdinalIgnoreCase) &&
                     !await MatchesAsync(target, f.Sha256, token)) ||
                    (manifest.Transport != "zip-chunks-v1" &&
                     !await MatchesAsync(target, f.Sha256, token))) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken token)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, token)).Equals(expected,
            StringComparison.OrdinalIgnoreCase);
    }
}
