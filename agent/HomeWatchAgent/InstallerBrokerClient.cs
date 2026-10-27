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

    private static async Task<string> RequestAsync(string request, CancellationToken cancellation)
    {
        using var pipe = new NamedPipeClientStream(".", "HomeWatchInstallerBroker",
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            await pipe.ConnectAsync(7000, cancellation);
            using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true)
                { AutoFlush = true };
            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            await writer.WriteLineAsync(request);
            string? reply = await reader.ReadLineAsync(cancellation);
            if (reply is null) throw new EndOfStreamException("Broker disconnected before replying");
            if (reply.Length > 2500) throw new IOException("Broker returned an oversized response");
            if (reply.StartsWith("ERROR ", StringComparison.Ordinal))
                throw new BrokerRejectionException(reply[6..]);
            return reply;
        }
        catch (BrokerRejectionException ex)
        {
            throw new IOException("Installer broker refused the request: " + ex.Message, ex);
        }
        catch (IOException ex) when (ex is not BrokerRejectionException)
        {
            throw new IOException("Installer broker connection failed: " + ex.Message +
                ". Check the service and broker audit log at " +
                @"C:\Program Files\HomeWatch\.installer-broker\audit.log", ex);
        }
    }

    internal static async Task<int> InstallElevatedAsync(int catalogueId, Action<string> progress,
        CancellationToken cancellation, Action? protectedCopyReady = null)
    {
        if (catalogueId <= 0) throw new ArgumentException("Invalid catalogue ID");
        // A child can grant desktop access to ONLY the specific broker-created
        // account; the account SID is independently obtained from the local service.
        string identity = await RequestAsync("IDENTITY", cancellation);
        if (!identity.StartsWith("ID S-1-5-21-", StringComparison.Ordinal))
            throw new IOException("Broker has no provisioned administrator identity. Open Installer broker setup as administrator.");
        InstallerDesktopAccess.AllowRunner(identity[3..]);
        progress("Verifying protected package and starting approved administrator installer…");
        string reply = await RequestAsync("RUN " + catalogueId, cancellation);
        if (!reply.StartsWith("STARTED ", StringComparison.Ordinal) ||
            !Guid.TryParseExact(reply[8..], "N", out _))
            throw new IOException("Broker did not confirm elevated installer launch: " + reply);
        string ticket = reply[8..];
        // STARTED is sent only after the broker has completed its own verified
        // protected copy. The normal user's downloaded cache is no longer needed.
        protectedCopyReady?.Invoke();
        progress("Elevated installer is open — follow the setup wizard.");
        while (true)
        {
            // Polling is independent of the original IPC connection, allowing
            // the broker to serve the status of other installations concurrently.
            await Task.Delay(2000, cancellation);
            string state = await RequestAsync("STATUS " + ticket, cancellation);
            if (state == "RUNNING") continue;
            if (state.StartsWith("EXIT ", StringComparison.Ordinal) &&
                int.TryParse(state[5..], out int exit)) return exit;
            throw new IOException("Unexpected elevated installer status: " + state);
        }
    }

    internal static async Task<string> PrepareAsync(int catalogueId, CancellationToken cancellation)
    {
        if (catalogueId <= 0) throw new ArgumentException("Invalid catalogue ID");
        string reply = await RequestAsync(catalogueId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellation);
        if (!reply.StartsWith("OK ", StringComparison.Ordinal)) throw new IOException("Unexpected staging response");
        string location = Path.GetFullPath(reply[3..]);
        var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            ".installer-broker", "staged")) + Path.DirectorySeparatorChar;
        if (!location.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(location))
            throw new IOException("Broker returned an invalid staging path");
        return location;
    }

    private sealed class BrokerRejectionException : IOException
    {
        public BrokerRejectionException(string message) : base(message) { }
    }

}
