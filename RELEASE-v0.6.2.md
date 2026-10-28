# HomeWatch v0.6.2 — Child-facing Storage Cleanup

Patch release to the working v0.6.1 interactive elevated-installer service. Does not change elevation authorisations, the parental Web UI policy, the enrolled device identity, the runner's protected credential, or the updater's three-executable archive.

## New Launcher features

- The ordinary **HomeWatch Launcher** has a **Storage · X GB** action in its bottom bar, next to (not inside) Performance settings. It refreshes automatically about every 45 seconds.
- **Storage Cleanup** displays a breakdown of the user's downloaded/legacy installers, and the privileged broker's verified stages. It also shows the **eligible-to-clean** amount separately from the total; recent and busy files may not be eligible yet.
- **Clean up now** manually clears eligible temporary files with one confirmation. It never touches NAS originals, installed games, game saves, broker configuration, or the runner credentials. There is no UAC prompt.
- During an active install initiated by this Launcher, the tidy-up action is disabled. On-disk per-package exclusive leases prevent concurrent HomeWatch instances from deleting an actively used download.
- For legacy or in-progress downloads left by old builds without leases, folders must be at least ten minutes old before a manual tidy-up. Recently modified folders are retained. Background abandoned-cache pruning after 48 hours is preserved.
- The signed broker handles two new *pathless* named-pipe requests: `STORAGE` returns total/eligible bytes, while `CLEAN` removes only safe broker-minted staging directories. A stage still being copied, a running installer, a stage within two minutes of its known installer exit, or an unknown stage changed in the past fifteen minutes is excluded. The broker still checks for active processes from the staging directory and refuses to traverse links. The ordinary Windows client never receives administrative file-system deletion rights.
- When the broker is stopped or unavailable, the UI explicitly says its size is unavailable; it does not falsely claim zero staging usage. The local download cleanup remains usable.

## Deployment

1. Deploy the full repository to `main`, confirm signed GitHub Actions build, then tag **`v0.6.2`**.
2. The normal signed three-binary updater will update the Launcher and local-cache controls, but **the broker also changed**. Run the newly signed HomeWatch Windows installer once as administrator on each broker-equipped PC to upgrade the fourth executable.
3. Open HomeWatch Launcher under the child's standard account. Select **Storage · X GB**, choose **Refresh size**, and run **Clean up now**. Check the reported amount reclaimed and verify installed games still run.
4. For a concurrency test, start a large approved download and confirm the active package isn't eligible for cleanup from another Launcher instance. When the setup finishes, the existing automatic cleanup runs as before.

## Safety notes

Temporary sizes are logical file lengths, not Windows on-disk allocated cluster sizes; the actual free-space delta may vary. The broker performs best-effort cleanup and can leave files that are locked, linked, very recent or still in use. Its scheduled retry and recovery from v0.6.1 continue working. An installer that spawns detached helpers outside the staging directory may retain dependencies; HomeWatch cannot perfectly infer that, so retain the 2-minute / 15-minute safety periods and test representative installers.

This release has not been compiled or run on Windows in the authoring environment. The GitHub Actions build and the Windows client/service integration remain the final validation steps.
