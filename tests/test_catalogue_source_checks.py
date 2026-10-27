from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_windows_client_never_passes_a_nas_url_to_privileged_updater():
    source = (ROOT / 'agent/HomeWatchAgent/AppPortalForm.cs').read_text()
    assert 'HttpClientHandler { AllowAutoRedirect = false }' in source
    assert 'SHA256.HashDataAsync' in source
    assert 'HashMatchesAsync(target, current.InstallerSha256)' in source
    assert 'UseShellExecute = true, WorkingDirectory = root' in source
    assert 'agent/catalogue/' in source
    assert 'HomeWatchService.exe' not in source
    assert 'Process.Start(new ProcessStartInfo(target)' in source


def test_release_version_and_three_executable_update_format_unchanged():
    assert '<Version>0.5.8</Version>' in (ROOT / 'agent/HomeWatchAgent/HomeWatchAgent.csproj').read_text()
    workflow = (ROOT / '.github/workflows/release.yml').read_text()
    assert "$version = '0.5.8'" in workflow
    for binary in ('HomeWatchAgent.exe','HomeWatchService.exe','HomeWatchUpdater.exe'):
        assert binary in workflow
