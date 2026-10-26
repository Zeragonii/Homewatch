using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HomeWatchAgent;

// Local, interactive, normal-user removal only. Not exposed to the privileged updater.
internal static class HouseholdGameUninstaller
{
    internal sealed record RegisteredUninstaller(string DisplayName, string Command, string InstallLocation);

    internal static RegisteredUninstaller? FindRegistered(string gameExe, string installRoot)
    {
        if (!File.Exists(gameExe)) return null;
        string executable = Path.GetFullPath(gameExe);
        string? root = Directory.Exists(installRoot) ? Path.GetFullPath(installRoot).TrimEnd('\\') : null;
        var matches = new List<RegisteredUninstaller>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var parent = RegistryKey.OpenBaseKey(hive, view)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (parent is null) continue;
                foreach (var name in parent.GetSubKeyNames().Take(2500))
                {
                    using var key = parent.OpenSubKey(name);
                    var command = key?.GetValue("UninstallString") as string;
                    if (string.IsNullOrWhiteSpace(command)) continue;
                    var location = (key?.GetValue("InstallLocation") as string ?? "").Trim().Trim('"');
                    var displayIcon = (key?.GetValue("DisplayIcon") as string ?? "").Trim().Trim('"');
                    bool match = false;
                    if (root is not null && location.Length > 0 && Path.IsPathFullyQualified(location) && !location.StartsWith(@"\\"))
                        match = Path.GetFullPath(location).TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase);
                    // Some installations provide DisplayIcon but no InstallLocation.
                    if (!match && displayIcon.Length > 0)
                        match = displayIcon.TrimEnd('"').Split(',')[0].Trim('"')
                            .Equals(executable, StringComparison.OrdinalIgnoreCase);
                    if (match) matches.Add(new RegisteredUninstaller(
                        key?.GetValue("DisplayName") as string ?? "Game", command, location));
                }
            }
            catch (Exception) { /* Unreadable registration must not block safe folder-removal checks. */ }
        }
        // More than one matching product is ambiguous; never choose an arbitrary uninstaller.
        return matches.Distinct().Count() == 1 ? matches[0] : null;
    }

    internal static async Task RunRegisteredAsync(RegisteredUninstaller entry)
    {
        // UninstallString is an OS-registered command, but never invoke a shell interpreter.
        // MSI registry entries may use /I; change that to /X to actually uninstall.
        string command = entry.Command.Trim();
        string filename;
        var argv = SplitWindowsCommandLine(command);
        if (argv.Length == 0) throw new IOException("Invalid registered uninstall command");
        string[] arguments;
        if (Path.GetFileName(argv[0]).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(argv[0]).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            // MSI entries often register /I (maintenance mode); use /X instead.
            var product = Regex.Match(command, @"\{[0-9a-fA-F-]{36}\}");
            if (!product.Success) throw new IOException("MSI entry does not specify a product code");
            filename = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            arguments = ["/x", product.Value];
        }
        else
        {
            filename = argv[0];
            if (!Path.IsPathFullyQualified(filename) || !filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(filename) || (File.GetAttributes(filename) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Registered uninstaller is missing or not an absolute executable path");
            var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "rundll32.exe" };
            if (forbidden.Contains(Path.GetFileName(filename)))
                throw new IOException("This uninstall command needs manual review by an administrator");
            arguments = argv.Skip(1).ToArray();
        }
        var start = new ProcessStartInfo(filename) { UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(filename) ?? Environment.SystemDirectory };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Windows could not start the registered uninstaller");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"Uninstaller returned exit code {process.ExitCode}");
    }

    // Renders precisely the command line according to Windows quoting rules.
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static string[] SplitWindowsCommandLine(string command)
    {
        IntPtr ptr = CommandLineToArgvW(command, out int count);
        if (ptr == IntPtr.Zero) return [];
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(ptr); }
    }

    internal static string? SafeDeleteRoot(string installRoot, string gameExe, IEnumerable<string> configuredRoots)
    {
        if (!File.Exists(gameExe) || !Path.IsPathFullyQualified(gameExe) || gameExe.StartsWith(@"\\")) return null;
        var exe = Path.GetFullPath(gameExe);
        var roots = configuredRoots.Where(x => !string.IsNullOrWhiteSpace(x) && Path.IsPathFullyQualified(x) &&
            !x.StartsWith(@"\\")).Select(Path.GetFullPath).Select(x => x.TrimEnd('\\')).ToList();
        if (roots.Count == 0) return null;
        string? gameRoot = null;
        if (!string.IsNullOrWhiteSpace(installRoot) && Path.IsPathFullyQualified(installRoot) && !installRoot.StartsWith(@"\\"))
            gameRoot = Path.GetFullPath(installRoot).TrimEnd('\\');
        if (gameRoot is null)
        {
            // Legacy installs: infer exactly one immediate child of a configured game library.
            var candidates = roots.Select(root => exe.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                ? exe[(root.Length + 1)..].Split('\\')[0] : "")
                .Select((name, index) => name.Length > 0 ? Path.Combine(roots[index], name) : "")
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 1) gameRoot = candidates[0];
        }
        if (gameRoot is null || !Directory.Exists(gameRoot) ||
            !exe.StartsWith(gameRoot + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        // Deletion is limited to one complete, dedicated game folder directly beneath
        // a parent-configured games directory. Never remove Program Files or a shared root.
        if (!roots.Any(root => string.Equals(Path.GetDirectoryName(gameRoot)?.TrimEnd('\\'),
                root, StringComparison.OrdinalIgnoreCase))) return null;
        // Reject junctions/symlinks anywhere in the tree and on ancestors; no traversal.
        var cursor = new DirectoryInfo(gameRoot);
        while (cursor is not null)
        {
            if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            cursor = cursor.Parent;
        }
        var dirs = new Stack<string>();
        dirs.Push(gameRoot);
        int inspected = 0;
        while (dirs.Count > 0)
        {
            string current = dirs.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (++inspected > 100_000) return null; // Large or surprising trees require manual review.
                var attr = File.GetAttributes(entry);
                if ((attr & FileAttributes.ReparsePoint) != 0) return null;
                if ((attr & FileAttributes.Directory) != 0) dirs.Push(entry);
            }
        }
        var homewatch = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
        if (gameRoot.Equals(homewatch, StringComparison.OrdinalIgnoreCase) ||
            gameRoot.StartsWith(homewatch + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        return gameRoot;
    }
}
