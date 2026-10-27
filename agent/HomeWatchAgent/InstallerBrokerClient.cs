using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace HomeWatchAgent;

// Installation-only staging IPC. The privileged service receives ONLY a
// catalogue ID, not an executable path, destination, arguments or command.
internal static class InstallerBrokerClient
{
    internal static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory,
        "HomeWatchInstallerBroker.exe"));

    internal static void ConfigureWithAdministrator()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "HomeWatchInstallerBroker.exe");
        if (!File.Exists(exe)) throw new IOException("Install HomeWatch v0.5.9 or newer as administrator once to add the broker service (the normal auto-updater cannot install this new service)");
        // This is a one-time local administrative enrolment, not a server command.
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas",
            Arguments = "--enrol", WorkingDirectory = AppContext.BaseDirectory });
    }

    internal static async Task<string> PrepareAsync(int catalogueId, CancellationToken cancellation)
    {
        if (catalogueId <= 0) throw new ArgumentException("Invalid catalogue ID");
        using var pipe = new NamedPipeClientStream(".", "HomeWatchInstallerBroker",
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(7000, cancellation);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var reader = new StreamReader(pipe);
        await writer.WriteLineAsync(catalogueId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string? reply = await reader.ReadLineAsync(cancellation);
        if (reply is null || reply.Length > 2500 || !reply.StartsWith("OK ", StringComparison.Ordinal))
            throw new IOException(reply?.StartsWith("ERROR ") == true ? reply[6..] :
                "Installer broker did not return a verified staging location");
        // Never trust a pipe path without the precise broker-owned staging prefix.
        string location = Path.GetFullPath(reply[3..]);
        var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            ".installer-broker", "staged")) + Path.DirectorySeparatorChar;
        if (!location.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(location)) throw new IOException("Broker returned an invalid staging path");
        return location;
    }
}
