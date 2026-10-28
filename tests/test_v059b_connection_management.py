from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def test_server_connection_form_is_locally_administrator_gated():
    settings = (ROOT / "agent/HomeWatchAgent/DefenderSetup.cs").read_text()
    program = (ROOT / "agent/HomeWatchAgent/Program.cs").read_text()
    assert 'Text = "Server connection..."' in settings
    assert "new ServerConnectionForm()" in settings
    assert 'new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)' in settings
    assert 'DefenderSetup.AdminArgument' in program


def test_reprovision_without_identity_reset_and_authentication_check():
    source = (ROOT / "agent/HomeWatchAgent/ServerConnectionForm.cs").read_text()
    assert 'File.ReadAllText(ConfigPath)' in source
    assert 'current.ServerUrl = newUrl;' in source
    assert 'File.Replace(temp, ConfigPath, null)' in source
    assert 'GetAsync("agent/catalogue", HttpCompletionOption.ResponseHeadersRead)' in source
    assert 'original.DeviceId' in source and 'original.DeviceToken' in source
    assert 'http.DefaultRequestHeaders.Authorization' in source
    assert 'current.DeviceToken != original.DeviceToken' in source
    assert 'current.DeviceId != original.DeviceId' in source
    assert 'current.InstallationId != original.InstallationId' in source
    assert 'current.EnrollmentSecret != original.EnrollmentSecret' in source
    assert 'new HttpClientHandler' not in source  # no certificate validation bypass


def test_existing_agent_detects_server_change_in_place():
    program = (ROOT / "agent/HomeWatchAgent/Program.cs").read_text()
    assert 'connectionRefreshTimer.Tick += (_, _) => RefreshConnectionFromDisk();' in program
    assert 'appPortal.Close()' in program
    assert 'new WellbeingManager(http, Request,' in program
    assert 'config = changed;' in program
    assert 'previous.Dispose();' in program


def test_broker_still_requires_https():
    broker = (ROOT / "agent/HomeWatchInstallerBroker/Program.cs").read_text()
    assert 'url.Scheme != Uri.UriSchemeHttps' in broker
    assert 'uri.Scheme != "https"' in broker
    assert 'Server connection to save and test the HTTPS URL' in broker


def test_docker_is_http_only_unless_proxy_terminates_tls():
    dockerfile = (ROOT / "server/Dockerfile").read_text()
    assert '"--port", "8000"' in dockerfile
    assert 'ssl-certfile' not in dockerfile and 'ssl-keyfile' not in dockerfile


def test_new_hotfix_numeric_versions_match():
    version = "0.6.3"
    assert f'<Version>{version}</Version>' in (ROOT / 'agent/HomeWatchAgent/HomeWatchAgent.csproj').read_text()
    assert f"$version = '{version}'" in (ROOT / '.github/workflows/release.yml').read_text()
