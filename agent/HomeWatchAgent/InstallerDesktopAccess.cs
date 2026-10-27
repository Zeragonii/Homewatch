using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HomeWatchAgent;

// Applied by the interactive, logged-in user's launcher. A separately logged-on
// administrator needs explicit permission to open this session's WinSta0 and
// Default desktop. This does not elevate the launcher or launch a process.
internal static class InstallerDesktopAccess
{
    internal static void AllowRunner(string runnerSid)
    {
        var sid = new SecurityIdentifier(runnerSid);
        if (!sid.IsAccountSid() || sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
            throw new UnauthorizedAccessException("Invalid dedicated installer identity");
        // Open objects from inside THIS user's Windows session, not session 0.
        IntPtr station = OpenWindowStation("WinSta0", false, 0x60000);
        if (station == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open user window station security");
        try
        {
            IntPtr desktop = OpenDesktop("Default", 0, false, 0x60000);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open user desktop security");
            try
            {
                AddAccess(station, sid, 0x000F037F); // WINSTA_ALL_ACCESS + standard rights
                AddAccess(desktop, sid, 0x000F01FF); // DESKTOP_ALL_ACCESS + standard rights
            }
            finally { CloseDesktop(desktop); }
        }
        finally { CloseWindowStation(station); }
    }

    private static void AddAccess(IntPtr handle, SecurityIdentifier sid, uint access)
    {
        byte[] binary = new byte[sid.BinaryLength];
        sid.GetBinaryForm(binary, 0);
        IntPtr sidPtr = Marshal.AllocHGlobal(binary.Length);
        IntPtr oldSecurity = IntPtr.Zero, updatedAcl = IntPtr.Zero;
        try
        {
            Marshal.Copy(binary, 0, sidPtr, binary.Length);
            uint result = GetSecurityInfo(handle, 7, 4, out _, out _, out IntPtr oldAcl, out _, out oldSecurity);
            if (result != 0) throw new Win32Exception((int)result, "Cannot read interactive desktop permissions");
            var rule = new EXPLICIT_ACCESS
            {
                grfAccessPermissions = access, grfAccessMode = 1, grfInheritance = 0,
                Trustee = new TRUSTEE { TrusteeForm = 0, TrusteeType = 1, ptstrName = sidPtr }
            };
            result = SetEntriesInAcl(1, ref rule, oldAcl, out updatedAcl);
            if (result != 0) throw new Win32Exception((int)result, "Cannot grant installer runner desktop access");
            result = SetSecurityInfo(handle, 7, 4, IntPtr.Zero, IntPtr.Zero, updatedAcl, IntPtr.Zero);
            if (result != 0) throw new Win32Exception((int)result, "Cannot update interactive desktop permissions");
        }
        finally
        {
            if (updatedAcl != IntPtr.Zero) LocalFree(updatedAcl);
            if (oldSecurity != IntPtr.Zero) LocalFree(oldSecurity);
            Marshal.FreeHGlobal(sidPtr);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct TRUSTEE
    { public IntPtr pMultipleTrustee; public uint MultipleTrusteeOperation; public uint TrusteeForm; public uint TrusteeType; public IntPtr ptstrName; }
    [StructLayout(LayoutKind.Sequential)] private struct EXPLICIT_ACCESS
    { public uint grfAccessPermissions; public uint grfAccessMode; public uint grfInheritance; public TRUSTEE Trustee; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenWindowStation(string name, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseWindowStation(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern uint SetSecurityInfo(IntPtr handle, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll", EntryPoint = "SetEntriesInAclW", SetLastError = true)] private static extern uint SetEntriesInAcl(int count, ref EXPLICIT_ACCESS entries, IntPtr oldAcl, out IntPtr newAcl);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
}
