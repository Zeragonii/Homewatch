from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AGENT = (ROOT / 'agent/HomeWatchAgent/AppPortalForm.cs').read_text()
CLIENT = (ROOT / 'agent/HomeWatchAgent/InstallerBrokerClient.cs').read_text()
CACHE = (ROOT / 'agent/HomeWatchAgent/InstallerCacheMaintenance.cs').read_text()
BROKER = (ROOT / 'agent/HomeWatchInstallerBroker/Program.cs').read_text()


def test_multifile_cache_deleted_after_install_no_question():
    assert 'InstallerCacheMaintenance.DeletePackage(item.Id, item.InstallerSha256)' in AGENT
    assert 'Remove the copied installer files to free up disk space?' not in AGENT
    assert 'cacheSafeToRemove' in AGENT
    assert 'finally\n        {\n            // Only delete after the unprivileged wizard exits' in AGENT


def test_dont_delete_local_download_during_broker_copy():
    assert 'cacheSafeToRemove = false;' in AGENT
    assert '() => cacheSafeToRemove = true' in AGENT
    assert 'protectedCopyReady?.Invoke()' in CLIENT
    assert CLIENT.index('STARTED ', CLIENT.index('InstallElevatedAsync')) < CLIENT.index('protectedCopyReady?.Invoke()')


def test_non_elevated_setup_waits_for_exit():
    start = AGENT.index('if (item.ElevateInstaller)')
    segment = AGENT[AGENT.index('installerExit = await InstallerBrokerClient.InstallElevatedAsync', start):AGENT.index('auditStatus = "started"', start)]
    assert 'await process.WaitForExitAsync();' in segment
    assert 'cacheSafeToRemove = true;' in segment


def test_legacy_installer_also_waits_and_cleans():
    legacy = AGENT[AGENT.index('private async Task InstallLegacyCatalogueAsync'):]
    assert 'await process.WaitForExitAsync();' in legacy
    assert 'InstallerCacheMaintenance.DeleteLegacy(legacySha)' in legacy


def test_local_cleanup_is_scoped_and_does_not_traverse_links():
    assert 'SpecialFolder.LocalApplicationData' in CACHE
    assert '"Packages"' in CACHE and '"installers"' in CACHE
    assert 'Regex PackageName' in CACHE and 'Regex LegacyName' in CACHE
    assert 'FileAttributes.ReparsePoint' in CACHE
    assert 'Directory.Delete(member, recursive: false)' in CACHE
    assert 'DateTime.UtcNow - (age ?? TimeSpan.FromDays(2))' in CACHE
    assert 'InstallerCacheMaintenance.PruneAbandoned()' in AGENT


def test_broker_cleanup_after_exit_with_busy_process_retry():
    assert 'InstallerExitGrace = TimeSpan.FromMinutes(2)' in BROKER
    assert 'await RetryCleanupAsync(stagedExe)' in BROKER
    assert 'process.MainModule?.FileName?.StartsWith' in BROKER
    assert 'if (!IsStageInUse(stagedExe))' in BROKER
    assert 'Directory.Delete(directory, recursive: false)' in BROKER


def test_broker_recovers_after_service_restart():
    assert 'StageMaintenanceAsync(stoppingToken)' in BROKER
    assert 'AbandonedStageAge = TimeSpan.FromHours(24)' in BROKER
    assert 'Task.Delay(TimeSpan.FromMinutes(30), stop)' in BROKER
    assert 'StageName.IsMatch(name)' in BROKER
    assert 'CopyingStages.ContainsKey(directory)' in BROKER
    assert 'Runs.Values.Any(run => !run.Complete)' in BROKER


def test_broker_cleans_up_when_launch_fails():
    assert 'if (!launched)' in BROKER
    assert 'CleanupStage(entry)' in BROKER
    assert 'stage-copy-cleanup-error' in BROKER
    assert 'await VerifyReadyToLaunchAsync(id, entry, stop)' in BROKER


def test_patch_version_and_compatibility():
    assert '<Version>0.6.4</Version>' in (ROOT / 'agent/HomeWatchAgent/HomeWatchAgent.csproj').read_text()
    assert '<Version>0.6.4</Version>' in (ROOT / 'agent/HomeWatchInstallerBroker/HomeWatchInstallerBroker.csproj').read_text()
    assert "$version = '0.6.4'" in (ROOT / '.github/workflows/release.yml').read_text()
    assert '#define MyAppVersion "0.6.4"' in (ROOT / 'agent/installer/HomeWatchAgent.iss').read_text()
    assert 'HomeWatchInstallerBroker-v0.6.0' in (ROOT / 'agent/HomeWatchInstallerBroker/RunnerIdentity.cs').read_text() # DPAPI entropy fixed across updates
    assert "@('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in (ROOT / '.github/workflows/release.yml').read_text()
