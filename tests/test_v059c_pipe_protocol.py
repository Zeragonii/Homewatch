from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BROKER = (ROOT/'agent/HomeWatchInstallerBroker/Program.cs').read_text()
CLIENT = (ROOT/'agent/HomeWatchAgent/InstallerBrokerClient.cs').read_text()
WORKFLOW = (ROOT/'.github/workflows/release.yml').read_text()


def test_client_duplex_streams_leave_pipe_open_for_reply():
    assert 'leaveOpen: true' in CLIENT
    assert 'ReadLineAsync(cancellation)' in CLIENT
    assert 'BrokerRejectionException' in CLIENT
    assert 'Installer broker connection failed' in CLIENT
    assert 'audit.log' in CLIENT


def test_broker_opens_writer_before_read_and_identity_checks():
    source = BROKER[BROKER.index('private static async Task HandleAsync('):BROKER.index('private static bool SafeRelative(')]
    assert source.index('using var writer = new StreamWriter') < source.index('pipe.RunAsClient(')
    assert 'catch (Exception ex)' in source
    assert 'await writer.WriteLineAsync("ERROR "' in source
    assert 'requestTimeout.CancelAfter(TimeSpan.FromSeconds(20))' in source
    assert 'pipe.RunAsClient(' in source
    assert 'int.TryParse(catalogue, out id)' in source
    assert 'BrokerSettings.Audit("denied catalogue="' in source


def test_service_side_broker_update_requires_installer():
    iss = (ROOT/'agent/installer/HomeWatchAgent.iss').read_text()
    assert 'Source: "..\\..\\out\\agent\\HomeWatchInstallerBroker.exe"' in iss
    assert "@('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in WORKFLOW
    assert 'HomeWatchInstallerBroker.exe' in WORKFLOW
    assert "'0.6.5'" in WORKFLOW
