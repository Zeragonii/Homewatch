from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AGENT = ROOT / 'agent/HomeWatchAgent'


def test_locally_requested_uac_is_separate_from_agent_mutex():
    source = (AGENT / 'Program.cs').read_text()
    dispatch = source.index('DefenderSetup.AdminArgument')
    mutex = source.index('new Mutex(true, SingleAgentMutex')
    assert dispatch < mutex
    assert 'DefenderSetup.OpenWithUac()' in source
    assert 'menu.Items.Add("Performance settings (admin)..."' in source
    assert 'performanceButton.Click += (_, _) => DefenderSetup.OpenWithUac()' in (AGENT / 'AppPortalForm.cs').read_text()


def test_embedded_constrained_script_requires_windows_admin():
    project = (AGENT / 'HomeWatchAgent.csproj').read_text()
    script = (ROOT / 'tools/Manage-DefenderExclusions.ps1').read_text()
    helper = (AGENT / 'DefenderSetup.cs').read_text()
    assert 'EmbeddedResource' in project and 'HomeWatch.Manage-DefenderExclusions.ps1' in project
    assert 'GetManifestResourceStream(ResourceName)' in helper
    assert 'WindowsBuiltInRole.Administrator' in helper
    assert 'Verb = "runas"' in helper
    assert '-ConfirmedInLocalUi' in helper
    assert '-NonInteractive' in helper
    assert 'RemoteSigned' in helper
    assert 'Add-MpPreference' in script and 'Remove-MpPreference' in script
    assert "[ValidateSet('Add', 'Remove')]" in script
    assert '[switch]$ConfirmedInLocalUi' in script
    assert "if ($ConfirmedInLocalUi)" in script
    assert 'Get-SafeGamePath' in script
    assert "Refusing to exclude an operating-system/application directory:" in script


def test_remote_service_not_modified_to_initiate_exclusions():
    service = (ROOT / 'agent/HomeWatchService/Program.cs').read_text()
    updater = (ROOT / 'agent/HomeWatchUpdater/Program.cs').read_text()
    assert 'Add-MpPreference' not in service + updater
    assert 'DefenderSetup' not in service + updater
    assert 'Manage-DefenderExclusions.ps1' not in service + updater


def test_version_and_three_executable_update_contract():
    workflow = (ROOT / '.github/workflows/release.yml').read_text()
    assert "$version = '0.6.0'" in workflow
    assert '<Version>0.6.0</Version>' in (AGENT / 'HomeWatchAgent.csproj').read_text()
    assert '#define MyAppVersion "0.6.0"' in (ROOT / 'agent/installer/HomeWatchAgent.iss').read_text()
    assert "@('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in workflow
    assert "Validate bundled Defender PowerShell syntax" in workflow
