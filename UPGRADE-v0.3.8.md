# Deploying v0.3.8

1. Back up `C:\ProgramData\HomeWatch\config.json` and the Postgres database.
2. Push this repo to a test branch first and verify Windows agent **and service** compile in Actions.
3. Test the installer on a spare Windows 11 machine with a standard child account and administrator account.
4. When ready, tag `v0.3.8` to publish public release artifacts.
5. Run `HomeWatch-Agent-Setup-win-x64.exe` once as administrator on each existing PC. This registers `HomeWatchUpdaterService` under LocalSystem.
6. Check `services.msc` to confirm `HomeWatchUpdaterService` is running, and sign into a standard user account to confirm tray/heartbeat.
7. Future new public GitHub releases are polled automatically every 15 minutes; pressing Update now writes a check request processed by the service, typically within a few seconds.
8. If updating a live session, the tray agent attempts a delayed relaunch. If it remains closed, log out/in to start it via HKLM Run.

### Known limitations

This is a first implementation: unsigned packages are checked by matching SHA-256 on your public GitHub release, **not Authenticode**. Do not deploy until Windows runner compilation and isolated-PC update testing succeeds. The service follows the latest non-prerelease GitHub Release; server-side channel pinning and rollback are not yet implemented. It does not support private repositories. A single failed file-copy may require repair via the elevated installer; transactional rollback remains future work.
