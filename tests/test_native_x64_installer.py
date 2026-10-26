from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_inno_uses_native_program_files_mode():
    iss = (ROOT / 'agent/installer/HomeWatchAgent.iss').read_text()
    assert 'ArchitecturesAllowed=x64compatible' in iss
    assert 'ArchitecturesInstallIn64BitMode=x64compatible' in iss
    assert 'UsePreviousAppDir=no' in iss
    assert 'DefaultDirName={autopf}\\HomeWatch' in iss
    assert 'DisableDirPage=yes' in iss


def test_legacy_x86_installer_blocks_conflicting_upgrade():
    iss = (ROOT / 'agent/installer/HomeWatchAgent.iss').read_text()
    assert 'function InitializeSetup(): Boolean;' in iss
    assert "ExpandConstant('{commonpf32}\\HomeWatch')" in iss
    assert "FileExists(AddBackslash(LegacyDir) + 'HomeWatchService.exe')" in iss


def test_service_install_dir_is_not_hardcoded():
    source = (ROOT / 'agent/HomeWatchService/Program.cs').read_text()
    assert 'Environment.ProcessPath' in source
    assert 'Directory.GetParent(directory)?.FullName' in source
    assert 'public static readonly string InstallDir = FindInstallDir()' in source
    assert 'ProgramFiles), "HomeWatch"' not in source


def test_agent_status_uses_its_own_directory():
    source = (ROOT / 'agent/HomeWatchAgent/Program.cs').read_text()
    assert 'Path.Combine(AppContext.BaseDirectory, ".updates", "status.json")' in source


def test_numeric_release_version_for_existing_updater():
    workflow = (ROOT / '.github/workflows/release.yml').read_text()
    assert "$version = '0.5.5'" in workflow
    assert '<Version>0.5.5</Version>' in (ROOT / 'agent/HomeWatchAgent/HomeWatchAgent.csproj').read_text()
