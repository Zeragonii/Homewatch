from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
BROKER = (ROOT / 'agent/HomeWatchInstallerBroker/Program.cs').read_text()
WORKFLOW = (ROOT / '.github/workflows/release.yml').read_text()
ISS = (ROOT / 'agent/installer/HomeWatchAgent.iss').read_text()
AGENT = (ROOT / 'agent/HomeWatchAgent/AppPortalForm.cs').read_text()
CLIENT = (ROOT / 'agent/HomeWatchAgent/InstallerBrokerClient.cs').read_text()


def test_independent_broker_is_built_and_signed():
    assert 'HomeWatchInstallerBroker.csproj' in WORKFLOW
    assert 'out/agent/HomeWatchInstallerBroker.exe' in WORKFLOW
    assert 'Source: "..\\..\\out\\agent\\HomeWatchInstallerBroker.exe"' in ISS
    assert 'HomeWatchInstallerBroker' in ISS


def test_three_binary_updater_still_unchanged():
    assert "@('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in WORKFLOW
    assert 'HomeWatchInstallerBroker.exe' not in (ROOT/'agent/HomeWatchService/Program.cs').read_text()


def test_narrow_pipe_input_and_server_authorisation():
    assert 'int.TryParse(line, out id)' in BROKER
    assert 'agent/elevation/installer/{id}' in BROKER
    assert 'pipe.RunAsClient(' in BROKER
    assert 'file.GetProperty("sha256")' in BROKER
    assert 'hash.GetHashAndReset()' in BROKER
    assert 'RunAsClient' in BROKER
    assert 'CreateProcessAsUser' not in BROKER
    assert 'Process.Start(new ProcessStartInfo(' not in BROKER


def test_broker_requires_credential_protected_https_pin():
    assert 'Uri.UriSchemeHttps' in BROKER
    assert 'broker-config.json' in BROKER
    assert '"/inheritance:r"' in BROKER
    assert '"*S-1-5-18:F"' in BROKER
    assert '"*S-1-5-32-544:F"' in BROKER


def test_user_cannot_supply_paths_or_args_to_service():
    assert 'WriteLineAsync(catalogueId.ToString(' in CLIENT
    assert 'InstallAsync(item' in AGENT
    assert 'InstallerBrokerClient.PrepareAsync(item.Id' in AGENT
    runtime = (ROOT/'agent/HomeWatchAgent/RuntimeBroker.cs').read_text()
    assert 'configPath = Path.Combine(AppContext.BaseDirectory, ".installer-broker", "broker-config.json")' in runtime
    assert 'Verb = "runas"' in runtime
