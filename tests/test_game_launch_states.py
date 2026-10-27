"""v0.5.6 process tracking and duplicate-launch prevention source contracts.

These tests complement, but don't substitute for, a Windows dotnet build and
live process integration testing in a normal desktop session.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AGENT = ROOT / 'agent/HomeWatchAgent'


def test_both_launch_buttons_reserve_game_before_awaiting_verification():
    source = (AGENT / 'AppPortalForm.cs').read_text()
    for method, end, path in (
        ('private async Task LaunchAsync(', 'private async Task InstallationStateAsync(', 'app.FilePath'),
        ('private async Task LaunchInstalledAsync(', 'private async Task ChooseInstalledAsync(', 'app.InstalledPath'),
    ):
        segment = source[source.index(method):source.index(end, source.index(method))]
        assert f'gameProcesses.TryBeginLaunch({path})' in segment
        assert segment.index(f'gameProcesses.TryBeginLaunch({path})') < segment.index('await ApplicationCandidate.ScanAsync')
        assert f'gameProcesses.CancelLaunch({path})' in segment
        assert f'gameProcesses.StateFor({path}) == GameProcessTracker.GameState.Running' in segment
        assert 'RuntimeBroker.Start("game", ' in segment and 'Process.Start(new ProcessStartInfo(' in segment


def test_launch_state_ui_refreshes_independently_of_network_polling():
    source = (AGENT / 'AppPortalForm.cs').read_text()
    assert 'gameStateTimer = new System.Windows.Forms.Timer { Interval = 1500 }' in source
    assert 'gameStateTimer.Tick += (_, _) => RefreshGameStates();' in source
    assert 'gameStateTimer.Stop(); gameStateTimer.Dispose()' in source
    assert '"◷  STARTING…"' in source
    assert '"■  STOP GAME"' in source
    assert '"◷  STOPPING…"' in source
    assert '"▶  PLAY"' in source
    assert 'var state = StateFor(app);' in source and 'elevatedGames' in source


def test_stop_requires_confirmation_and_never_uses_a_name_only_kill():
    source = (AGENT / 'AppPortalForm.cs').read_text()
    tracker = (AGENT / 'GameProcessTracker.cs').read_text()
    assert 'MessageBoxButtons.YesNo, MessageBoxIcon.Warning' in source[source.index('private async Task StopGameAsync('):source.index('private async Task LaunchAsync(')]
    assert 'gameProcesses.TryBeginStop(path)' in source
    assert 'Task.Run(() => gameProcesses.ForceStop(path))' in source
    assert 'QueryFullProcessImageName' in tracker
    assert 'Process.GetProcessesByName(name)' in tracker
    assert 'process.SessionId != sessionId' in tracker
    assert 'string.Equals(GetExecutablePath(process), path, StringComparison.OrdinalIgnoreCase)' in tracker
    assert 'process.Kill(entireProcessTree: true)' in tracker
    assert 'Process.GetProcessesByName("*"' not in tracker
    assert 'Process.Start("taskkill' not in tracker


def test_signing_release_keeps_same_three_executable_format_and_new_version():
    workflow = (ROOT / '.github/workflows/release.yml').read_text()
    assert "$version = '0.5.9.2'" in workflow
    assert 'osslsigncode' in workflow
    assert 'HOMEWATCH_SIGNING_PFX_BASE64' in workflow
    for name in ('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe'):
        assert name in workflow
    assert '<Version>0.5.9.2</Version>' in (AGENT / 'HomeWatchAgent.csproj').read_text()


def test_running_game_cannot_be_uninstalled_from_launcher():
    source = (AGENT / 'AppPortalForm.cs').read_text()
    assert 'currentUninstallButton.Enabled = state == GameProcessTracker.GameState.Ready && !installing' in source
    uninstall = source[source.index('private async Task UninstallCatalogueAsync('):source.index('private async Task InstallCatalogueAsync(')]
    assert 'gameProcesses.StateFor(app.InstalledPath) != GameProcessTracker.GameState.Ready' in uninstall
    assert 'Close the game before uninstalling it.' in uninstall
