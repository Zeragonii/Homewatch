from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
AGENT = ROOT / 'agent/HomeWatchAgent'


def test_multifile_package_stream_hash_during_copy():
    src = (AGENT / 'HouseholdPackageInstaller.cs').read_text()
    assert 'IncrementalHash.CreateHash(HashAlgorithmName.SHA256)' in src
    assert 'sha.AppendData(buffer, 0, read);' in src
    assert 'sha.GetHashAndReset()' in src
    assert 'File.Move(temp, destination, true)' in src
    assert src.index('sha.GetHashAndReset()') < src.index('File.Move(temp, destination, true)')
    assert 'Verifying the complete package' not in src
    # SHA256.HashDataAsync is still used to check an already-cached file once.
    assert 'SHA256.HashDataAsync(fs, token)' in src


def test_legacy_installer_hashes_during_transfer():
    src = (AGENT / 'AppPortalForm.cs').read_text()
    assert 'sha.AppendData(buffer, 0, size);' in src
    assert 'private static async Task<string> CopyLimitedAsync' in src
    assert 'hash = await CopyLimitedAsync(input, output, maxLength)' in src
    # Before setup launch, recheck only the EXE, never a full-package second pass.
    assert 'Approved setup executable changed after staging' in src


def test_exclusions_require_explicit_local_administrator_action():
    script = (ROOT / 'tools/Manage-DefenderExclusions.ps1').read_text()
    assert '#Requires -RunAsAdministrator' in script
    assert 'SupportsShouldProcess = $true' in script
    assert "[ValidateSet('Add', 'Remove')]" in script
    assert '[Parameter(Mandatory = $true)]' in script
    assert '$GameFolders' in script
    assert 'Get-CimInstance Win32_UserProfile' in script
    assert 'Add-MpPreference -ExclusionPath $path' in script
    assert 'Remove-MpPreference -ExclusionPath $path' in script
    assert 'Set-MpPreference' not in script
    for project in ('HomeWatchAgent/Program.cs', 'HomeWatchService/Program.cs', 'HomeWatchUpdater/Program.cs'):
        assert 'Add-MpPreference' not in (ROOT / 'agent' / project).read_text()


def test_release_bumps_agent_not_server_api_contract():
    assert '<Version>0.6.2</Version>' in (AGENT / 'HomeWatchAgent.csproj').read_text()
    assert "$version = '0.6.2'" in (ROOT / '.github/workflows/release.yml').read_text()
    assert '#define MyAppVersion "0.6.2"' in (ROOT / 'agent/installer/HomeWatchAgent.iss').read_text()
    workflow = (ROOT / '.github/workflows/release.yml').read_text()
    for required in ('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe'):
        assert required in workflow
