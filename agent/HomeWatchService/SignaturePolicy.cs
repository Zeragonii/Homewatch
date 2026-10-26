using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Only certificates approved locally by an elevated administrator are trusted.
// This class deliberately NEVER fetches trust material from HomeWatch's web server.
internal static class SignaturePolicy
{
    private static string PinFile => Path.Combine(Settings.State, "trusted-publisher.sha256");
    private static readonly Guid VerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static string GetPinnedPublisher()
    {
        if (!File.Exists(PinFile))
            throw new InvalidOperationException("No local HomeWatch signing certificate has been approved. Open HomeWatch Launcher > Performance settings > Signing certificate as an administrator to establish trust before the next update.");
        string pin = File.ReadAllText(PinFile).Trim().ToUpperInvariant();
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit))
            throw new InvalidDataException("Local publisher trust pin is malformed; refusing unsigned/unverified updates");
        return pin;
    }

    internal static void VerifyAll(string folder)
    {
        string pin = GetPinnedPublisher();
        foreach (string name in Settings.Allowed)
            VerifyFile(Path.Combine(folder, name), pin);
    }

    private static void VerifyFile(string file, string expectedSha256)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Missing signed update executable", file);
        // WinVerifyTrust checks the Authenticode file digest AND the Windows certificate chain.
        // Merely reading an embedded certificate without WinVerifyTrust is NOT sufficient.
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>() };
        fileInfo.pcwszFilePath = Marshal.StringToHGlobalUni(file);
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var trust = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0,
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = filePointer,
                dwStateAction = 0, // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x1010 // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
            };
            Guid policy = VerifyV2;
            int status = WinVerifyTrust(IntPtr.Zero, ref policy, ref trust);
            if (status != 0)
                throw new InvalidDataException($"Authenticode verification failed for {Path.GetFileName(file)} (0x{(uint)status:X8}). Ensure the local publisher certificate is trusted and the file is not modified.");
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
            string actual = Convert.ToHexString(SHA256.HashData(signer.RawData));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update signer mismatch for {Path.GetFileName(file)}. Local administrator approval of a different publisher is required.");
        }
        finally
        {
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeHGlobal(fileInfo.pcwszFilePath);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}
