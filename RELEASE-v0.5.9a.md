# HomeWatch v0.5.9a — Installer broker setup button UI fix

Internal version / release tag: **0.5.9.1**, required by the existing numeric updater.

## Fixed

- The elevated **Performance settings** window previously allotted only 38 pixels for five buttons. At normal Windows display sizes, the **Installer broker setup...** button wrapped to an invisible second row.
- The actions now occupy two visible rows: **Add exclusions / Remove exclusions / Signing certificate** followed by **Installer broker setup / Close**.
- The dialog is slightly taller to preserve the Defender output panel.
- The broker-missing error now explains that an administrator must run the full installer once: the ordinary signed HomeWatch update ZIP deliberately includes only the original three executables.

## Rollout

1. Push this complete repository to main, verify GitHub Actions, and tag **`v0.5.9.1`** to build/sign the UI patch.
2. The existing agent can auto-update to 0.5.9.1 normally if the signing trust pin has been established.
3. If the PC has *only* received the 0.5.9 agent auto-update, run the new signed **HomeWatch-Agent-Setup-win-x64.exe** once as administrator to install/register `HomeWatchInstallerBroker.exe`.
4. From the Launcher choose **Performance settings**, approve UAC, then click **Installer broker setup...** on the second row.
5. Use Windows PowerShell as administrator and `Get-Service HomeWatchInstallerBroker` to verify service presence.

This release does not add password-free elevated game installation. It retains the broker's protected staging and separate UAC runtime path from 0.5.9.
