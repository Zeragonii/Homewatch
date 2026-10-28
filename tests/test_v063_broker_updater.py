from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVICE = (ROOT / 'agent/HomeWatchService/Program.cs').read_text()
SIG = (ROOT / 'agent/HomeWatchService/SignaturePolicy.cs').read_text()
WORKFLOW = (ROOT / '.github/workflows/release.yml').read_text()
INSTALLER = (ROOT / 'agent/installer/HomeWatchAgent.iss').read_text()


def test_existing_three_file_release_archive_is_still_compatible():
    # The deployed v0.6.2 updater accepts exactly the original 3 executable names.
    # Adding the broker to that ZIP would prevent this bridge release installing.
    assert "@('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in WORKFLOW
    assert 'public static readonly string[] Allowed = { "HomeWatchAgent.exe", "HomeWatchUpdater.exe", "HomeWatchService.exe" };' in SERVICE
    packaging = WORKFLOW.split('      - name: Package exact updater binaries', 1)[1].split('      - name: Install Inno Setup', 1)[0]
    assert "Compress-Archive -LiteralPath $paths" in packaging
    assert "'HomeWatchInstallerBroker.exe'" not in packaging.split('Compress-Archive', 1)[0].split('$allowed = @(', 1)[1].split(')', 1)[0]


def test_broker_has_its_own_sha256_release_assets():
    assert 'Copy-Item out/agent/HomeWatchInstallerBroker.exe HomeWatchInstallerBroker-win-x64.exe' in WORKFLOW
    assert 'HomeWatchInstallerBroker-win-x64.sha256' in WORKFLOW
    assert 'HomeWatchInstallerBroker-win-x64.exe' in WORKFLOW
    assert WORKFLOW.count('            HomeWatchInstallerBroker-win-x64.exe') == 2  # artifact and tagged GitHub release
    assert 'out/agent/HomeWatchInstallerBroker.exe' in WORKFLOW.split('      - name: Sign Windows binaries', 1)[1]


def test_broker_update_cannot_bypass_admin_approved_signer():
    assert 'SignaturePolicy.VerifyBroker(staged);' in SERVICE
    assert 'SignaturePolicy.VerifyBroker(source);' in SERVICE
    assert 'SignaturePolicy.VerifyBroker(installed);' in SERVICE
    assert 'VerifyFile(brokerFile, GetPinnedPublisher())' in SIG
    assert 'WinVerifyTrust' in SIG
    assert 'signedVersion' in SERVICE and 'suppliedVersion' in SERVICE
    assert 'SHA-256 mismatch' in SERVICE
    assert 'Trusted(download, tag)' in SERVICE and 'Trusted(checksum, tag)' in SERVICE


def test_broker_update_waits_until_main_signed_updater_has_been_upgraded():
    assert 'await CheckBrokerAsync(root, tag, remote, ct)' in SERVICE
    assert 'if (Settings.Canonical(remote) == Settings.Canonical(installed))' in SERVICE
    assert 'Settings.Canonical(currentVersion) >= Settings.Canonical(remote)' in SERVICE
    assert 'await ApplyUpdate.Run(args[2], args[3]);' in SERVICE
    assert 'await BrokerUpdate.Run(args[2], args[3]);' in SERVICE


def test_privileged_broker_update_is_transactional():
    assert 'await BrokerUpdate.VerifyServiceRegistration();' in SERVICE
    assert 'registered).Equals(target, StringComparison.OrdinalIgnoreCase)' in SERVICE
    assert 'await WaitState(1)' in SERVICE and 'await WaitState(4)' in SERVICE
    assert 'File.Replace(source, installed, backup' in SERVICE
    assert 'File.Replace(backup, installed, null' in SERVICE
    assert 'SignaturePolicy.VerifyBroker(installed)' in SERVICE
    assert 'if (BrokerUpdate.InstallationInProgress())' in SERVICE
    assert 'if (InstallationInProgress())' in SERVICE
    assert 'DateTime.UtcNow.AddMinutes(-10)' in SERVICE
    assert 'FileAttributes.ReparsePoint' in SERVICE
    assert 'broker-config.json' not in SERVICE
    assert 'RunnerIdentity' not in SERVICE


def test_initial_install_still_sets_up_broker_service_and_keeps_unchanged_credentials():
    assert 'create HomeWatchInstallerBroker' in INSTALLER
    assert 'config HomeWatchInstallerBroker' in INSTALLER
    assert 'Source: "..\\..\\out\\agent\\HomeWatchInstallerBroker.exe"' in INSTALLER
    assert '#define MyAppVersion "0.6.3.1"' in INSTALLER


def test_stale_broker_updater_artifacts_are_narrowly_scavenged():
    assert 'BrokerUpdate.CleanupStaleArtifacts();' in SERVICE
    assert 'Regex.IsMatch(name, @"^broker-[a-fA-F0-9]{32}$")' in SERVICE
    assert 'FileAttributes.ReparsePoint' in SERVICE
    assert 'Directory.GetCreationTimeUtc(directory) >= cutoff' in SERVICE
    assert 'File.GetCreationTimeUtc(path) >= cutoff' in SERVICE
    assert 'File.Delete(path);' in SERVICE
