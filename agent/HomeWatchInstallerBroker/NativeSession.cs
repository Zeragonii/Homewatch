using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

internal static class NativeSession
{
    internal static bool TryGetCallerSession(NamedPipeServerStream pipe, string callerSid, out uint session)
    {
        session = 0;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0 ||
            !ProcessIdToSessionId(pid, out uint clientSession) || clientSession == 0)
            return false;
        // The signed-in console user must match the impersonated pipe client;
        // don't let a background process in another session launch a GUI here.
        if (WTSGetActiveConsoleSessionId() != clientSession ||
            !WTSQueryUserToken(clientSession, out IntPtr token)) return false;
        try
        {
            using var identity = new WindowsIdentity(token);
            if (identity.User?.Value != callerSid) return false;
            session = clientSession;
            return true;
        }
        finally { CloseHandle(token); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
