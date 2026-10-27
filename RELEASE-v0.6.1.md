# HomeWatch v0.6.1 — Installer cache cleanup

Patch release to the working v0.6.0 password-free interactive installer broker. **No changes to the parent approval model, HTTPS broker enrolment, game elevation, database schema or signed three-executable update archive.**

## Changes

- The Launcher automatically deletes the approved multi-file download cache under `%LOCALAPPDATA%\HomeWatch\Packages\<catalogue-id>-<sha256>` once it has completed its use of it. This includes successful installations, nonzero setup exits, and failures to locate a game executable after the wizard closes. The old yes/no deletion prompt is removed.
- A broker request that might still be copying from this cache does **not** trigger deletion. The Launcher only considers it safe once the broker has returned `STARTED`, which means it completed its own protected copy. Ambiguous broker disconnections are retained for later safe pruning.
- Non-elevated packages are cleaned only after their interactive installer exits. Legacy single-file installers in `%LOCALAPPDATA%\HomeWatch\installers` now also wait for their wizard to exit before cleanup.
- On Launcher startup, directories from old abandoned user downloads older than **48 hours** are pruned. Only strictly named HomeWatch cache directories are eligible; local installed game locations and NAS sources are never targeted.
- The SYSTEM broker cleans its unique protected stage under `%ProgramFiles%\HomeWatch\.installer-broker\staged` **2 minutes after the launcher installer process exits**, provided no executables still run from it. Busy/locked folders are retried every minute. A maintenance sweep every 30 minutes cleans orphaned stages that are at least **24 hours** old (including across service restarts).
- If policy revalidation or process creation fails after staging, the unused protected stage is removed immediately. Older stage-only IPC requests receive a 6-hour compatibility window before cleanup.
- Local-cache cleanup does not cross reparse-point directory links. Broker cleanup also deletes directory links themselves, not their targets.

## Deploy

1. Push the v0.6.1 repository and let GitHub Actions build/sign as normal.
2. Publish the versioned release: `git tag v0.6.1 && git push origin v0.6.1`.
3. **Run the newly signed Windows installer as administrator once on each PC with the broker.** The ordinary updater replaces `HomeWatchAgent.exe`, `HomeWatchService.exe`, and `HomeWatchUpdater.exe` only; it does not yet update the fourth binary, `HomeWatchInstallerBroker.exe`. The admin-run upgrade should preserve device enrolment, broker pinning and the existing HWInstallRunner identity. Do not manually delete ProgramData or the broker configuration.
4. Install an approved test package. After the wizard closes, verify the user package folder is removed. Within roughly two minutes, verify the associated protected broker stage is removed. A helper still running from that stage or a locked file may extend the delay.

## Notes and limitations

- A setup wizard can start helpers elsewhere and exit early. The broker detects executables running directly from staging and retries locked-file failures; it cannot guarantee that an externally launched helper no longer needs an unlocked package file. The two-minute grace period is conservative but not absolute. Test representative installers before assuming early-exit helpers are handled perfectly.
- If the Windows service is killed immediately before deferred cleanup, its startup maintenance recovers abandoned stages after 24 hours. Existing stages from prior releases will also be pruned when old enough.
- If a cleanup fails because Windows still holds a file, HomeWatch retains it for retry/stale-cache maintenance rather than risking corruption.
- The legacy single-file installer path changes from fire-and-forget to waiting for the installer to exit before reenabling its Install button. This is intentional to ensure safe deletion.
- No Windows compilation was available in the build environment; confirm signed GitHub Actions publish and actual live cache cleanup on one client before wider rollout.
