using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HomeWatchAgent;

// One tracker per interactive launcher. It never accepts an arbitrary process name
// as proof a game is running: the executable's full path must match exactly.
internal sealed class GameProcessTracker
{
    internal enum GameState { Ready, Starting, Running, Stopping }

    private sealed class PendingAction
    {
        public GameState State { get; set; }
        public DateTime SinceUtc { get; set; }
    }

    private readonly Dictionary<string, PendingAction> actions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private readonly int sessionId = GetCurrentSessionId();

    private static int GetCurrentSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    public GameState StateFor(string executable)
    {
        var path = Normalize(executable);
        if (path is null) return GameState.Ready;
        if (actions.TryGetValue(path, out var action) && action.State == GameState.Stopping)
            return GameState.Stopping;
        // An existing process wins over an old startup reservation.
        if (FindMatchingProcesses(path).Count > 0)
        {
            actions.Remove(path);
            return GameState.Running;
        }
        if (!actions.TryGetValue(path, out action)) return GameState.Ready;
        if (DateTime.UtcNow - action.SinceUtc > StartTimeout)
        {
            actions.Remove(path);
            return GameState.Ready;
        }
        return GameState.Starting;
    }

    public bool TryBeginLaunch(string executable)
    {
        var path = Normalize(executable);
        if (path is null || StateFor(path) != GameState.Ready) return false;
        actions[path] = new PendingAction { State = GameState.Starting, SinceUtc = DateTime.UtcNow };
        return true;
    }

    public void CancelLaunch(string executable)
    {
        var path = Normalize(executable);
        if (path is not null && actions.TryGetValue(path, out var action) && action.State == GameState.Starting)
            actions.Remove(path);
    }

    public bool TryBeginStop(string executable)
    {
        var path = Normalize(executable);
        if (path is null || StateFor(path) != GameState.Running) return false;
        actions[path] = new PendingAction { State = GameState.Stopping, SinceUtc = DateTime.UtcNow };
        return true;
    }

    public void EndStop(string executable)
    {
        var path = Normalize(executable);
        if (path is not null) actions.Remove(path);
    }

    // Only terminate processes in this Windows session whose executable path
    // matches the selected approved game. A same-named EXE in another directory
    // must be left untouched. Recheck immediately before killing each PID.
    public int ForceStop(string executable)
    {
        var path = Normalize(executable) ?? throw new IOException("Invalid game executable path");
        int stopped = 0;
        foreach (var id in FindMatchingProcesses(path))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.SessionId != sessionId ||
                    !string.Equals(GetExecutablePath(process), path, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                stopped++;
            }
            catch (ArgumentException) { /* Already exited. */ }
            catch (InvalidOperationException) { /* Already exited. */ }
        }
        return stopped;
    }

    private List<int> FindMatchingProcesses(string fullPath)
    {
        var matched = new List<int>();
        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(name)) return matched;
        Process[] processes;
        try { processes = Process.GetProcessesByName(name); }
        catch { return matched; }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId) continue;
                    if (string.Equals(GetExecutablePath(process), fullPath, StringComparison.OrdinalIgnoreCase))
                        matched.Add(process.Id);
                }
                catch (InvalidOperationException) { /* Process exited during enumeration. */ }
                catch (System.ComponentModel.Win32Exception) { /* Cannot inspect this process. */ }
            }
        }
        return matched;
    }

    private static string? Normalize(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        try
        {
            var path = Path.GetFullPath(executable).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    // QueryFullProcessImageName supports PROCESS_QUERY_LIMITED_INFORMATION, which
    // generally also works for games started with an elevated Windows token.
    private static string? GetExecutablePath(Process process)
    {
        IntPtr handle = OpenProcess(0x1000, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var builder = new StringBuilder(32768);
            uint size = (uint)builder.Capacity;
            if (!QueryFullProcessImageName(handle, 0, builder, ref size)) return null;
            return Normalize(builder.ToString());
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
