from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_installer_lists_every_required_binary_explicitly():
    installer = (ROOT / "agent/installer/HomeWatchAgent.iss").read_text()
    assert 'Source: "..\\..\\out\\agent\\*"' not in installer
    for exe in ("HomeWatchAgent.exe", "HomeWatchService.exe", "HomeWatchUpdater.exe"):
        assert f'Source: "..\\..\\out\\agent\\{exe}"' in installer
    assert 'config HomeWatchUpdaterService binPath=' in installer


def test_service_reports_path_mismatch_before_install():
    source = (ROOT / "agent/HomeWatchService/Program.cs").read_text()
    assert "Environment.ProcessPath" in source
    assert "File.Exists(currentExecutable)" in source
    assert "currentExecutable).Equals(expectedExecutable" in source
    assert "File.Copy(currentExecutable, helper)" in source


def test_agent_marker_not_blocked_by_a_hardcoded_service_binary_path():
    source = (ROOT / "agent/HomeWatchAgent/Program.cs").read_text()
    start = source.index("async Task CheckUpdateAsync(")
    end = source.index("void ShowAppPortal()", start)
    assert "HomeWatchService.exe" not in source[start:end]
    assert 'check-update.request' in source[start:end]
