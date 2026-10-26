# HomeWatch v0.5.3 — Local administrator performance settings

This version adds a **Performance settings** button to HomeWatch Launcher and a **Performance settings (admin)** tray menu action. Neither is a remote server command.

## Behaviour

1. A local user explicitly opens Performance settings.
2. Windows UAC requests administrator approval; HomeWatch never handles the password.
3. The administrator reviews the optional game directory and chooses **Add exclusions** or **Remove exclusions**, with a second confirmation.
4. The bundled PowerShell exclusion script applies the changes only to the local PC and reports errors in the window.

Supported exclusions are the `Packages` and `installers` staging directories under HomeWatch in **existing Windows user profiles**, and optionally one selected existing local game directory. The script does not exclude Windows directories or `C:\Program Files\HomeWatch`, and rejects system folders, network paths, wildcards, drive roots and Program Files locations. A new Windows user profile needs its cache exclusion configured after profile creation.

**Important:** Folder exclusions leave all contents of those folders unscanned by Defender real-time scanning. This feature is optional and cannot be initiated by a web UI action, agent command, or the SYSTEM updater service. If Defender detects the HomeWatch binary itself, use signing and Microsoft’s false-positive submission process instead of automatically excluding the monitoring agent. Group Policy and tamper protection may prevent exclusions even with UAC approval.

No server migration or new updater files. The existing GitHub Release three-executable ZIP remains unchanged; the script is embedded as an agent assembly resource so the remote updater can deliver it. The original standalone `tools/Manage-DefenderExclusions.ps1` remains available.

## Release

1. Push updated repo to `main` and confirm GitHub Actions passes.
2. Tag `v0.5.3` and publish the release.
3. Use normal HomeWatch updater for Windows clients.
4. Open **HomeWatch Launcher → Performance settings** on a client and approve the UAC prompt to test add/remove with a small directory.

The Windows UI and Defender process have not been run in this Linux build environment. Test on a Windows standard-user account before wider rollout.
