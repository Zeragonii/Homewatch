from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BROKER = (ROOT / 'agent/HomeWatchInstallerBroker/Program.cs').read_text()
IDENTITY = (ROOT / 'agent/HomeWatchInstallerBroker/RunnerIdentity.cs').read_text()
SESSION = (ROOT / 'agent/HomeWatchInstallerBroker/NativeSession.cs').read_text()
CLIENT = (ROOT / 'agent/HomeWatchAgent/InstallerBrokerClient.cs').read_text()
DESKTOP = (ROOT / 'agent/HomeWatchAgent/InstallerDesktopAccess.cs').read_text()
PORTAL = (ROOT / 'agent/HomeWatchAgent/AppPortalForm.cs').read_text()
ISS = (ROOT / 'agent/installer/HomeWatchAgent.iss').read_text()
WORKFLOW = (ROOT / '.github/workflows/release.yml').read_text()


def test_user_approved_admin_identity_is_isolated_from_system_token():
    assert 'RunnerIdentity.Provision()' in BROKER
    assert 'Create the dedicated administrator identity?' in BROKER
    assert 'LocalMachine' in IDENTITY and 'ProtectedData.Protect' in IDENTITY
    assert 'NetUserAdd' in IDENTITY and 'NetLocalGroupAddMembers' in IDENTITY
    assert 'HWInstallRunner' in IDENTITY and 'private const string Name' in IDENTITY
    assert '"icacls.exe"' in BROKER and '"/inheritance:r"' in BROKER
    assert 'CreateProcessAsUser' in IDENTITY and 'LogonUser(' in IDENTITY
    assert 'TokenSessionId' in IDENTITY
    assert 'SeTcbPrivilege' in IDENTITY
    assert 'winsta0\\default' in IDENTITY
    assert 'LogonUser(Name, Environment.MachineName, password' in IDENTITY
    assert 'GetCurrentProcess()' in IDENTITY  # for privilege setup only


def test_client_cannot_choose_admin_path_arguments_or_sid():
    assert 'pipe.RunAsClient(' in BROKER
    assert 'NativeSession.TryGetCallerSession(pipe, sid' in BROKER
    assert 'WTSQueryUserToken' in SESSION
    assert 'WTSGetActiveConsoleSessionId() != clientSession' in SESSION
    assert 'identity.User?.Value != callerSid' in SESSION
    assert 'InstallerDesktopAccess.AllowRunner(identity[3..])' in CLIENT
    assert 'InstallerBrokerClient.InstallElevatedAsync(item.Id' in PORTAL
    assert 'RUN " + catalogueId' in CLIENT
    assert 'STATUS " + ticket' in CLIENT
    assert 'RequestAsync("IDENTITY"' in CLIENT
    assert 'Path.GetFullPath' not in BROKER[BROKER.index('private static async Task HandleAsync'):BROKER.index('private static async Task VerifyReadyToLaunchAsync')]
    assert 'OpenWindowStation("WinSta0"' in DESKTOP
    assert 'OpenDesktop("Default"' in DESKTOP


def test_service_revalidates_server_hash_immediately_before_run():
    assert 'StageApprovedAsync(sid, id, stop)' in BROKER
    assert 'VerifyReadyToLaunchAsync(id, entry, stop)' in BROKER
    assert 'agent/elevation/installer/{id}' in BROKER
    assert 'SHA256.HashDataAsync(stream, stop)' in BROKER
    assert 'protected' in BROKER.lower()
    assert 'STARTED ' in BROKER and 'RUNNING' in BROKER and 'EXIT ' in BROKER
    assert 'Installer broker refused the request' in CLIENT


def test_broker_service_upgrade_and_admin_account_uninstall():
    assert 'HomeWatchInstallerBroker.exe' in ISS
    assert 'RunnerConfigExists' in ISS
    assert '--remove-runner' in ISS and 'RunnerIdentity.Remove()' in BROKER
    assert 'NetUserDel' in IDENTITY
    assert "'0.6.1'" in WORKFLOW
    assert '@(\'HomeWatchAgent.exe\', \'HomeWatchService.exe\', \'HomeWatchUpdater.exe\')' in WORKFLOW
    assert 'HomeWatchInstallerBroker.exe' not in (ROOT/'agent/HomeWatchService/Program.cs').read_text()
