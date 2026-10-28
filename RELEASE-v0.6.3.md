# HomeWatch v0.6.3 — Installer Broker Remote Updates

## Why this release is compatible with v0.6.2

Prior to v0.6.3, `HomeWatchUpdaterService` intentionally accepted **exactly three** signed executables in `HomeWatch-Agent-win-x64.zip`. Simply adding a fourth entry would have caused existing clients to reject the update. This release deliberately maintains the same three-file update ZIP and includes a **separate, independently signed broker release asset**.

GitHub Actions now publishes:

- `HomeWatch-Agent-win-x64.zip` and `.sha256` — **unchanged three-file format** for the agent, Windows updater service and legacy helper executable.
- `HomeWatchInstallerBroker-win-x64.exe` and `.sha256` — the fourth signed executable, downloaded automatically by the upgraded updater service.
- The complete signed Windows setup installer and its checksum, for new installations and manual recovery.

The already-running v0.6.2 updater first installs the compatible three-file ZIP and restarts the newer updater service. The new service then detects that its local broker EXE is older than the current release, downloads the separate broker asset and stages its replacement. This works even though the release version matches the updated main agent version. No administrator-run reinstall is needed **on a PC where the broker service is already registered**.

## Security and rollback

- The broker asset must be fetched from the fixed GitHub release URL, match its published SHA-256, pass Windows Authenticode verification and match the **locally pinned, administrator-approved publisher certificate**. The signed broker's Windows file version must equal the release tag. All of these checks run before service replacement; signing and version checks repeat inside the SYSTEM-owned apply helper.
- Service registration must point to `HomeWatchInstallerBroker.exe` inside the existing HomeWatch installation folder. It will not follow an arbitrary service path or create a different privileged service.
- A running installer in the broker's protected staging area, or a stage modified in the previous 10 minutes, postpones replacement; the updater will retry at its next update check.
- The helper stops only `HomeWatchInstallerBroker`, waits for the service to stop, and **atomically replaces** the EXE on NTFS while maintaining the old binary as a rollback backup. After the new service starts it removes its protected temporary files. If replacement or service startup fails, it attempts to restore the prior executable and restart it.
- `.installer-broker/runner-identity.json`, encrypted credentials, broker enrolment/configuration and package staging remain in place. There is **no** new runner account, provisioning prompt, Windows installer invocation, or database migration.

## Update procedure

1. Push this complete repository to `main`. Ensure the Windows build, four-executable Authenticode signing, archive validation and automated tests pass.
2. Publish a stable GitHub release using the `v0.6.3` tag.
3. Let the existing updater discover the new release, or use the agent's **Check for updates** action to request a check.
4. Expect two phases: first the normal three-binary agent update; then the standalone broker update after the updater service restarts (typically its first check). The broker can be deferred if any approved installation is still active.
5. Check the service's diagnostic log in `C:\Program Files\HomeWatch\.updates\updater.log` and its `status.json` for `broker-downloading`, `broker-installing`, `installed`, `deferred`, or a precise failure.

Confirm the fourth EXE's version **after** the update:

```powershell
(Get-Item 'C:\Program Files\HomeWatch\HomeWatchInstallerBroker.exe').VersionInfo.FileVersion
Get-Service HomeWatchInstallerBroker
```

Expected version: `0.6.3.0` (the final zero may be omitted depending on Windows formatting), and a running service if it was running before the upgrade.

**Important:** This changes the Windows updater, not the HomeWatch server API. It doesn't need a new Docker container or database migration. It cannot install the broker on a device that never had it; the initial signed Windows installer still requires an administrator to register the service and approve the dedicated local execution identity exactly once.

## Validation boundaries

Source-level and API regression tests can check the three-file bridge, published assets, trust verification, service handling and rollback logic, but cannot validate Windows Service Control Manager behaviour, NTFS atomic replacement or Authenticode checks in this Linux-based environment. GitHub Actions is the first real Windows compilation; confirm the first live update on a single laptop before broad deployment.
