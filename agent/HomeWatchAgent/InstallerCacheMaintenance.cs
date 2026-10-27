using System.Text.RegularExpressions;

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
                            TryDelete(root, name, pattern);
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
