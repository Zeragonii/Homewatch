using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

// An isolated local administrator identity, created during an explicit elevated
// local provisioning operation. No parent password is stored or transmitted.
// Never launch a SYSTEM-token GUI on the child's desktop.
internal static class RunnerIdentity
{
    private const string Name = "HWInstallRunner";
    internal static readonly string FilePath = Path.Combine(BrokerSettings.Root, "runner-identity.json");
    private sealed record Credential(string Name, string Sid, string ProtectedPassword);

    internal static string Sid => Read().Sid;

    private static Credential Read()
    {
        var config = JsonSerializer.Deserialize<Credential>(System.IO.File.ReadAllText(FilePath))
            ?? throw new IOException("Installer runner configuration is missing; use Installer broker setup as administrator");
        if (config.Name != Name || !config.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid installer runner identity");
        var actual = (SecurityIdentifier)new NTAccount(Environment.MachineName, Name).Translate(typeof(SecurityIdentifier));
        if (actual.Value != config.Sid)
            throw new UnauthorizedAccessException("Installer runner account has changed; re-provision as administrator");
        return config;
    }

    internal static void Provision()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Local administrator approval is required");
        Directory.CreateDirectory(BrokerSettings.Root);
        if (System.IO.File.Exists(FilePath)) { _ = Read(); return; }
        // Reuse is intentionally forbidden: we must never overwrite or adopt a
        // pre-existing, potentially user-controlled local account.
        var existing = new NTAccount(Environment.MachineName, Name);
        try { _ = existing.Translate(typeof(SecurityIdentifier));
            throw new UnauthorizedAccessException("Account HWInstallRunner already exists without protected credentials. Remove it as administrator before enrolling."); }
        catch (IdentityNotMappedException) { }

        string password = "Hw!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var info = new USER_INFO_1
        {
            name = Name, password = password, priv = 1, comment = "HomeWatch parent-approved interactive installer runner",
            flags = 0x0001 | 0x0200 | 0x10000 // script-enabled normal account; non-expiring random password
        };
        int rc = NetUserAdd(null, 1, ref info, out _);
        if (rc != 0) throw new Win32Exception(rc, "Could not create dedicated HomeWatch installer account");
        try
        {
            var admin = (NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                .Translate(typeof(NTAccount));
            string group = admin.Value.Split('\\').Last();
            var member = new LOCALGROUP_MEMBERS_INFO_3 { domainandname = Environment.MachineName + "\\" + Name };
            rc = NetLocalGroupAddMembers(null, group, 3, ref member, 1);
            if (rc != 0) throw new Win32Exception(rc, "Could not assign installer runner administrator membership");
            var sid = ((SecurityIdentifier)existing.Translate(typeof(SecurityIdentifier))).Value;
            // ProtectedData is encrypted using a machine-local key. File ACL
            // separately restricts access to SYSTEM and local administrators.
            var encrypted = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(password),
                System.Text.Encoding.UTF8.GetBytes("HomeWatchInstallerBroker-v0.6.0"), DataProtectionScope.LocalMachine);
            string tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (System.IO.File.Create(tmp)) { }
                BrokerSettings.LockDownFile(tmp);
                System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(new Credential(Name, sid, Convert.ToBase64String(encrypted))));
                System.IO.File.Move(tmp, FilePath, false);
            }
            finally { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); }
            BrokerSettings.Audit("runner-created sid=" + sid);
        }
        catch
        {
            // Do not leave an unmanaged local administrator account if provisioning fails.
            NetUserDel(null, Name);
            throw;
        }
    }

    internal static void Remove()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Only an administrator can remove the installer runner");
        if (!System.IO.File.Exists(FilePath)) return;
        _ = Read(); // Check account SID before deleting anything.
        int result = NetUserDel(null, Name);
        if (result != 0 && result != 2221)
            throw new Win32Exception(result, "Could not remove installer runner account");
        System.IO.File.Delete(FilePath);
    }

    internal static LaunchedInstaller Launch(string verifiedExe, uint sessionId)
    {
        var config = Read();
        string password = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(config.ProtectedPassword),
            System.Text.Encoding.UTF8.GetBytes("HomeWatchInstallerBroker-v0.6.0"), DataProtectionScope.LocalMachine));
        if (!LogonUser(Name, Environment.MachineName, password, 2, 0, out IntPtr userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not log on dedicated installer administrator");
        try
        {
            // The full elevated token is required; refuse filtered UAC tokens.
            IntPtr elevated = IntPtr.Zero;
            IntPtr useToken = userToken;
            try
            {
                if (!IsElevated(userToken))
                {
                    if (!GetTokenInformation(userToken, 19, out elevated, IntPtr.Size, out _)
                        || elevated == IntPtr.Zero || !IsElevated(elevated))
                        throw new UnauthorizedAccessException("Installer runner did not receive an administrator token");
                    useToken = elevated;
                }
                if (!DuplicateTokenEx(useToken, 0xF01FF, IntPtr.Zero, 2, 1, out IntPtr primary))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not duplicate installer runner token");
                try
                {
                    EnsureSessionPrivilege();
                if (!SetTokenInformation(primary, 12, ref sessionId, sizeof(uint)))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not target the child's Windows session");
                    // Loading the dedicated profile avoids HKCU pointing at
                    // the service account's registry hive.
                    var profile = new PROFILEINFO { dwSize = Marshal.SizeOf<PROFILEINFO>(), lpUserName = Name };
                    if (!LoadUserProfile(primary, ref profile))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load installer runner profile");
                    try
                    {
                        if (!CreateEnvironmentBlock(out IntPtr environment, primary, false))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create runner environment");
                        try
                        {
                            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                            // lpApplicationName is explicit; no command shell,
                            // user-submitted parameters, or arbitrary working folder.
                            string command = "\"" + verifiedExe + "\"";
                            if (!CreateProcessAsUser(primary, verifiedExe, command, IntPtr.Zero, IntPtr.Zero,
                                false, 0x00000400, environment, Path.GetDirectoryName(verifiedExe)!, ref startup, out var created))
                                throw new Win32Exception(Marshal.GetLastWin32Error(),
                                    "Interactive installer launch failed (check desktop access and session permissions)");
                            CloseHandle(created.hThread);
                            // Runner profile and token must live until the process has exited.
                            return new LaunchedInstaller(created.hProcess, primary, profile.hProfile, created.dwProcessId);
                        }
                        finally { DestroyEnvironmentBlock(environment); }
                    }
                    catch { UnloadUserProfile(primary, profile.hProfile); throw; }
                }
                catch { CloseHandle(primary); throw; }
            }
            finally { if (elevated != IntPtr.Zero) CloseHandle(elevated); }
        }
        finally { CloseHandle(userToken); }
    }

    internal static void EnsureSessionPrivilege()
    {
        // TokenSessionId requires SeTcbPrivilege, held by LocalSystem but
        // potentially disabled in the service process token.
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out IntPtr token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open broker process token");
        try
        {
            if (!LookupPrivilegeValue(null, "SeTcbPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Session privilege is unavailable");
            var state = new TOKEN_PRIVILEGES { PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = 0x2 } };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) ||
                Marshal.GetLastWin32Error() == 1300)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Broker cannot enable required session privilege");
        }
        finally { CloseHandle(token); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disable, ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    private static bool IsElevated(IntPtr token) => GetTokenInformation(token, 20, out uint elevated, 4, out _) && elevated != 0;

    internal sealed class LaunchedInstaller(IntPtr process, IntPtr token, IntPtr profile, uint pid) : IDisposable
    {
        internal uint Pid => pid;
        internal int WaitForExit()
        {
            var result = WaitForSingleObject(process, 0xFFFFFFFF);
            if (result != 0 || !GetExitCodeProcess(process, out uint exit))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not obtain installer exit status");
            return unchecked((int)exit);
        }
        public void Dispose()
        {
            if (profile != IntPtr.Zero) UnloadUserProfile(token, profile);
            CloseHandle(process);
            CloseHandle(token);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct USER_INFO_1
    { public string name; public string password; public uint password_age; public uint priv; public string? home_dir; public string? comment; public uint flags; public string? script_path; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct LOCALGROUP_MEMBERS_INFO_3 { public string domainandname; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PROFILEINFO
    { public int dwSize; public int dwFlags; public string lpUserName; public string? lpProfilePath; public string? lpDefaultPath; public string? lpServerName; public string? lpPolicyPath; public IntPtr hProfile; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
    { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public uint dwX; public uint dwY; public uint dwXSize; public uint dwYSize; public uint dwXCountChars; public uint dwYCountChars; public uint dwFillAttribute; public uint dwFlags; public ushort wShowWindow; public ushort cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION
    { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetUserAdd(string? servername, int level, ref USER_INFO_1 buf, out uint parm_err);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetUserDel(string? servername, string username);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetLocalGroupAddMembers(string? servername, string groupname, int level, ref LOCALGROUP_MEMBERS_INFO_3 buf, int totalentries);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LogonUser(string username, string domain, string password, int logonType, int logonProvider, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int tokenInformationClass, out uint information, int size, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int tokenInformationClass, out IntPtr information, int size, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int informationClass, ref uint info, int length);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LoadUserProfile(IntPtr token, ref PROFILEINFO profile);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool UnloadUserProfile(IntPtr token, IntPtr profile);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(IntPtr token, string application, string command, IntPtr procAttrs, IntPtr threadAttrs, bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
