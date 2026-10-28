using System.Text.RegularExpressions;
using System.Security;

namespace HomeWatchAgent;

// Deletes only HomeWatch's own downloader folders; never installed game roots,
// any NAS share, or the machine-wide broker configuration/staging area.
internal static class InstallerCacheMaintenance
{
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeWatch");
    private static readonly Regex PackageName = new(@"^\d+-[a-f0-9]{64}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LegacyName = new(@"^[a-f0-9]{64}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool DeletePackage(int id, string sha256) => id > 0 &&
        Regex.IsMatch(sha256 ?? "", "^[a-f0-9]{64}$", RegexOptions.IgnoreCase) &&
        TryDelete(Path.Combine(CacheRoot, "Packages"), id + "-" + sha256, PackageName);

    internal static bool DeleteLegacy(string sha256) =>
        Regex.IsMatch(sha256 ?? "", "^[a-f0-9]{64}$", RegexOptions.IgnoreCase) &&
        TryDelete(Path.Combine(CacheRoot, "installers"), sha256, LegacyName);

    // A lock outside the cache folder prevents other HomeWatch instances and
    // the manual cleaner from removing a package during an active download or
    // while the installer/broker may still be reading it. This is intentionally
    // per *package*, not a global lock across the entire library.
    internal static IDisposable AcquirePackageLease(int id, string digest) =>
        AcquireLease("Packages", id + "-" + digest, PackageName)
        ?? throw new IOException("This installer package is already being used by another HomeWatch session");

    internal static IDisposable AcquireLegacyLease(string digest) =>
        AcquireLease("installers", digest, LegacyName)
        ?? throw new IOException("This installer is already being used by another HomeWatch session");

    private static FileStream? AcquireLease(string category, string name, Regex pattern)
    {
        if (!pattern.IsMatch(name)) throw new IOException("Invalid HomeWatch cache identity");
        string lockRoot = Path.Combine(CacheRoot, ".package-locks");
        if (!SafeCacheParent(CacheRoot) || !SafeCacheParent(lockRoot))
            throw new IOException("Unsafe HomeWatch cache directory");
        Directory.CreateDirectory(lockRoot);
        if (!SafeCacheParent(lockRoot)) throw new IOException("Unsafe cache lock directory");
        string lockPath = Path.Combine(lockRoot, category + "-" + name + ".lock");
        try
        {
            if (File.Exists(lockPath) &&
                (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                return null;
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool SafeCacheParent(string path) =>
        !Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    internal sealed record CacheUsage(long TotalBytes, long EligibleBytes, int FolderCount);
    internal sealed record CacheCleanup(long FreedBytes, int RemovedFolders);
    private static readonly TimeSpan ManualAge = TimeSpan.FromMinutes(10);

    // Show total occupied cache size even when it is too new / in use to
    // delete. Only strictly recognised cache folders are eligible.
    internal static CacheUsage GetUsage()
    {
        long total = 0, eligible = 0;
        int folders = 0;
        foreach (var (category, regex) in new[] { ("Packages", PackageName), ("installers", LegacyName) })
        {
            string root = Path.Combine(CacheRoot, category);
            if (!SafeCacheParent(CacheRoot) || !SafeCacheParent(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (string path in Directory.EnumerateDirectories(root))
                {
                    string name = Path.GetFileName(path);
                    if (!regex.IsMatch(name) || !SafeCacheParent(path)) continue;
                    long bytes = TreeBytes(path);
                    folders++;
                    total = checked(total + bytes);
                    if (Directory.GetLastWriteTimeUtc(path) < DateTime.UtcNow - ManualAge)
                    {
                        using var lease = AcquireLease(category, name, regex);
                        if (lease != null) eligible = checked(eligible + bytes);
                    }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new CacheUsage(total, eligible, folders);
    }

    // Manual cleanup never accepts filesystem paths from the UI. Never traverse
    // links, touch the actual installed game directory, or remove a recent or
    // locked package. The lease stays held until deletion is complete.
    internal static CacheCleanup CleanNow()
    {
        long freed = 0;
        int removed = 0;
        foreach (var (category, regex) in new[] { ("Packages", PackageName), ("installers", LegacyName) })
        {
            string root = Path.Combine(CacheRoot, category);
            if (!SafeCacheParent(CacheRoot) || !SafeCacheParent(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (string path in Directory.EnumerateDirectories(root))
                {
                    string name = Path.GetFileName(path);
                    if (!regex.IsMatch(name) || !SafeCacheParent(path)) continue;
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(path) >= DateTime.UtcNow - ManualAge) continue;
                        using var lease = AcquireLease(category, name, regex);
                        if (lease is null) continue;
                        long bytes = TreeBytes(path);
                        if (TryDelete(root, name, regex))
                        {
                            freed = checked(freed + bytes);
                            removed++;
                        }
                    }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new CacheCleanup(freed, removed);
    }

    private static long TreeBytes(string path)
    {
        long sum = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string member in Directory.EnumerateFileSystemEntries(directory))
                {
                    try
                    {
                        var attr = File.GetAttributes(member);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attr & FileAttributes.Directory) != 0) pending.Push(member);
                        else sum = checked(sum + new FileInfo(member).Length);
                    }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return sum;
    }

    private static bool TryDelete(string root, string name, Regex expected)
    {
        if (!expected.IsMatch(name)) return false;
        string folder = Path.Combine(root, name);
        try
        {
            // Parent directories are user-writable: reject any replacement
            // with a junction to another tree before deleting descendants.
            if ((Directory.Exists(CacheRoot) &&
                 (File.GetAttributes(CacheRoot) & FileAttributes.ReparsePoint) != 0) ||
                (Directory.Exists(root) &&
                 (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0))
                return false;
            if (!Directory.Exists(folder)) return true;
            // The downloaded cache is untrusted, even if it was initially
            // written by HomeWatch. Never recursively traverse reparse points.
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return false;
            DeleteTree(folder);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void DeleteTree(string directory)
    {
        foreach (string member in Directory.EnumerateFileSystemEntries(directory))
        {
            var attrs = File.GetAttributes(member);
            if ((attrs & FileAttributes.Directory) != 0)
            {
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(member, recursive: false); // remove link only
                else
                    DeleteTree(member);
            }
            else
            {
                // File.Delete removes a file symlink itself, not its target.
                File.Delete(member);
            }
        }
        Directory.Delete(directory, recursive: false);
    }

    // Called before loading the library: abandoned downloads from old agent
    // crashes are pruned, but recently downloaded/resumable packages are kept.
    internal static void PruneAbandoned(TimeSpan? age = null)
    {
        var cutoff = DateTime.UtcNow - (age ?? TimeSpan.FromDays(2));
        foreach (var (root, pattern) in new[] {
            (Path.Combine(CacheRoot, "Packages"), PackageName),
            (Path.Combine(CacheRoot, "installers"), LegacyName) })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string folder in Directory.EnumerateDirectories(root))
                {
                    string name = Path.GetFileName(folder);
                    if (!pattern.IsMatch(name)) continue;
                    try
                    {
                        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 &&
                            Directory.GetLastWriteTimeUtc(folder) < cutoff)
                        {
                            using var lease = AcquireLease(Path.GetFileName(root), name, pattern);
                            if (lease != null) TryDelete(root, name, pattern);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
