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
        // Broker connection is a single request/reply. The protocol never takes
        // executable paths, command lines, or a destination from the user.
        using var pipe = new NamedPipeClientStream(".", "HomeWatchInstallerBroker",
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            await pipe.ConnectAsync(7000, cancellation);
            // StreamReader/Writer must NOT own the underlying duplex pipe. Both
            // remain alive until the response has been read completely.
            using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false),
                bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            await writer.WriteLineAsync(catalogueId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string? reply = await reader.ReadLineAsync(cancellation);
            if (reply is null)
                throw new EndOfStreamException("The installer broker disconnected without sending a reply");
            if (reply.Length > 2500)
                throw new IOException("Installer broker returned an oversized reply");
            if (reply.StartsWith("ERROR ", StringComparison.Ordinal))
                throw new BrokerRejectionException(reply[6..]);
            if (!reply.StartsWith("OK ", StringComparison.Ordinal))
                throw new IOException("Installer broker returned an unexpected response");
            // Never trust a pipe path without the precise broker-owned staging prefix.
            string location = Path.GetFullPath(reply[3..]);
            var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                ".installer-broker", "staged")) + Path.DirectorySeparatorChar;
            if (!location.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(location)) throw new IOException("Broker returned an invalid staging path");
            return location;
        }
        catch (BrokerRejectionException ex)
        {
            throw new IOException("Installer broker refused the request: " + ex.Message, ex);
        }
        catch (IOException ex) when (ex is not BrokerRejectionException)
        {
            throw new IOException("Installer broker connection failed: " + ex.Message +
                ". Check HomeWatchInstallerBroker service status and the broker audit log in " +
                @"C:\Program Files\HomeWatch\.installer-broker\audit.log", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("Installer broker closed its connection unexpectedly. Check the broker audit log in " +
                @"C:\Program Files\HomeWatch\.installer-broker\audit.log", ex);
        }
    }

    private sealed class BrokerRejectionException : IOException
    {
        public BrokerRejectionException(string message) : base(message) { }
    }

}
