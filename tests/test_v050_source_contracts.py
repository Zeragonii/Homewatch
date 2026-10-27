from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]

def test_version_and_update_archive_format():
    assert '<Version>0.5.6</Version>' in (ROOT/'agent/HomeWatchAgent/HomeWatchAgent.csproj').read_text()
    assert '$version = \'0.5.6\'' in (ROOT/'.github/workflows/release.yml').read_text()
    workflow = (ROOT/'.github/workflows/release.yml').read_text()
    for exe in ('HomeWatchAgent.exe','HomeWatchService.exe','HomeWatchUpdater.exe'):
        assert exe in workflow


def test_nas_remote_browser_and_manifest_have_client_and_server_components():
    main = (ROOT/'server/app/main.py').read_text()
    client = (ROOT/'agent/HomeWatchAgent/HouseholdPackageInstaller.cs').read_text()
    assert "'/api/nas/browse'" in main and "'/api/nas/scan'" in main
    assert 'scan_package' in (ROOT/'server/app/nas_library.py').read_text()
    assert 'SHA256.HashDataAsync' in client
    assert 'FileMode.Open' in client
    assert 'WaitForExitAsync' in (ROOT/'agent/HomeWatchAgent/AppPortalForm.cs').read_text()


def test_no_elevation_from_system_service_and_no_docker_nas_mount():
    agent = (ROOT/'agent/HomeWatchAgent/AppPortalForm.cs').read_text()
    assert 'UseShellExecute = true' in agent
    assert 'Verb = "runas"' not in agent
    assert 'nas-installers' not in (ROOT/'docker-compose.yml').read_text()
