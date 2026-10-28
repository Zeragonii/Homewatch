from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UI = (ROOT / 'agent/HomeWatchAgent/AppPortalForm.cs').read_text()
FORM = (ROOT / 'agent/HomeWatchAgent/StorageCleanupForm.cs').read_text()
CACHE = (ROOT / 'agent/HomeWatchAgent/InstallerCacheMaintenance.cs').read_text()
CLIENT = (ROOT / 'agent/HomeWatchAgent/InstallerBrokerClient.cs').read_text()
BROKER = (ROOT / 'agent/HomeWatchInstallerBroker/Program.cs').read_text()


def test_launcher_has_visible_child_facing_storage_action():
    assert 'storageButton = ButtonFor("Storage · checking…"' in UI
    assert 'new StorageCleanupForm(() => installing)' in UI
    assert 'storageTimer' in UI and 'RefreshStorageBadgeAsync' in UI
    assert 'Performance settings' in UI
    assert 'Run the v0.6.2 Windows installer' in UI


def test_cleanup_form_displays_both_caches_and_eligible_bytes():
    assert 'Downloaded packages:' in FORM
    assert 'Protected broker staging:' in FORM
    assert 'Ready to clear safely:' in FORM
    assert 'Clean up now' in FORM
    assert 'if (busy || IsDisposed || installationActive() || eligibleBytes <= 0)' in FORM
    assert 'InstallerCacheMaintenance.CleanNow' in FORM
    assert 'InstallerBrokerClient.CleanStorageAsync' in FORM
    assert 'broker (unavailable)' in FORM


def test_installer_uses_per_package_leases_until_after_cleanup():
    assert 'cacheLease = InstallerCacheMaintenance.AcquirePackageLease(item.Id, item.InstallerSha256)' in UI
    assert UI.index('cacheLease = InstallerCacheMaintenance.AcquirePackageLease') < UI.index('HouseholdPackageInstaller.InstallAsync')
    assert UI.index('InstallerCacheMaintenance.DeletePackage(item.Id, item.InstallerSha256)') < UI.index('cacheLease?.Dispose()')
    assert 'legacyLease = InstallerCacheMaintenance.AcquireLegacyLease(legacySha)' in UI
    assert UI.index('InstallerCacheMaintenance.DeleteLegacy(legacySha)') < UI.index('legacyLease?.Dispose()')


def test_manual_cache_cleanup_is_narrow_and_cannot_remove_active_or_recent():
    assert 'PackageName = new(' in CACHE and 'LegacyName = new(' in CACHE
    assert 'FileShare.None' in CACHE
    assert 'ManualAge = TimeSpan.FromMinutes(10)' in CACHE
    assert 'using var lease = AcquireLease(category, name, regex);' in CACHE
    assert 'FileAttributes.ReparsePoint' in CACHE
    assert 'TryDelete(root, name, regex)' in CACHE
    assert 'Path.Combine(CacheRoot, "Packages")' in CACHE
    assert '"installers"' in CACHE


def test_broker_cleanup_commands_are_pathless_and_only_stages():
    assert 'RequestAsync("STORAGE", cancellation)' in CLIENT
    assert 'RequestAsync("CLEAN", cancellation)' in CLIENT
    assert 'if (line == "STORAGE")' in BROKER
    assert 'if (line == "CLEAN")' in BROKER
    assert 'StageName.IsMatch(Path.GetFileName(d))' in BROKER
    assert 'CopyingStages.ContainsKey(directory)' in BROKER
    assert 'Runs.Values.Any(r => !r.Complete)' in BROKER
    assert 'DateTime.UtcNow - r.FinishedUtc < InstallerExitGrace' in BROKER
    assert 'UnknownStageGrace = TimeSpan.FromMinutes(15)' in BROKER
    assert 'IsStageInUse(Path.Combine(directory, "setup.exe"))' in BROKER
    assert 'CleanupStage(Path.Combine(folder, "setup.exe"))' in BROKER
    assert 'StageTreeBytes' in BROKER


def test_broker_response_does_not_expose_paths_or_credentials():
    assert 'STORAGE {usage.TotalBytes} {usage.EligibleBytes}' in BROKER
    assert '"CLEANED " + freed' in BROKER
    assert 'CLEAN ' not in CLIENT  # No caller-provided parameters
    assert 'RunnerIdentity.LaunchedInstaller' in BROKER


def test_no_change_to_signed_update_archive_or_runner_password_entropy():
    workflow = (ROOT / '.github/workflows/release.yml').read_text()
    runner = (ROOT / 'agent/HomeWatchInstallerBroker/RunnerIdentity.cs').read_text()
    assert "@('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in workflow
    assert 'HomeWatchInstallerBroker-v0.6.0' in runner
