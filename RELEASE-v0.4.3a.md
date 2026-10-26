# HomeWatch v0.4.3a — Native x64 installer path correction

This is a small corrective patch on top of v0.4.3. It does **not** change the approval workflows or the launcher UI.

## Version / Git tag

The patch is called **v0.4.3a**. The underlying .NET service uses `System.Version`, which requires a numeric format; consequently **the release tag is `v0.4.3.1`**, not `v0.4.3a`.

## What changed

- Inno Setup explicitly selects native 64-bit install mode with `ArchitecturesInstallIn64BitMode=x64compatible`, not merely `ArchitecturesAllowed`. The default install directory is `C:\Program Files\HomeWatch`.
- `UsePreviousAppDir=no` prevents Inno from reusing the old x86 default path; directory selection is disabled to guarantee a consistent native path.
- Existing `C:\Program Files (x86)\HomeWatch` installations are detected and setup **stops safely** with instructions to uninstall the old copy first. Avoid running two HomeWatch installations or leaving conflicting uninstall entries.
- The privileged service now resolves its actual executable directory at runtime (and correctly resolves the parent directory when running as its own `.updates` helper). The agent reads update diagnostics from its own directory rather than assuming `C:\Program Files`.
- Machine-wide device identity (`C:\ProgramData\HomeWatch\config.json`) is not removed by the installer or uninstaller. Back it up before migration.

## One-time migration from the old x86 installation

1. **Back up** `C:\ProgramData\HomeWatch\config.json` somewhere safe. Do not delete or edit the device in the web admin UI.
2. From an administrator account, uninstall the old HomeWatch Agent via **Settings > Apps > Installed apps**. This removes the old updater service registration and the old Program Files (x86) binaries, but does not delete the shared device identity file.
3. Verify `C:\Program Files (x86)\HomeWatch\HomeWatchAgent.exe` is no longer present (if remnants remain, inspect the old uninstall before removing leftovers).
4. Run the verified v0.4.3.1 HomeWatch installer as administrator, without disabling Windows security features.
5. Verify all three EXEs exist at `C:\Program Files\HomeWatch`, and that `HomeWatchUpdaterService` points at that path.
6. Check your existing logical device remains enrolled and connected. You should **not** need to approve a new device.

## Verify installation (elevated PowerShell)

```powershell
Get-CimInstance Win32_Service -Filter "Name='HomeWatchUpdaterService'" | Select-Object Name, State, PathName
Get-ChildItem "$env:ProgramFiles\HomeWatch" -Filter 'HomeWatch*.exe' | Select-Object Name, Length
Get-Content "$env:ProgramFiles\HomeWatch\.updates\updater.log" -Tail 25 -ErrorAction SilentlyContinue
```

## Remote update testing

Because this patch repairs the installer itself, **one manual administrator-run installation is necessary** to move from the legacy x86 path. After the service is running from the native x64 path, test unattended updates **with a future numbered release** (`v0.4.4` or later). Press **Update now** in the admin UI and inspect the device card's updater status.

The current updater trusts a ZIP checksum served by the same unsigned GitHub release. This is not publisher-signature validation. Do not disable SmartScreen or Defender: use legitimately signed binaries, and investigate any security detection rather than bypassing protection.

## Build validation

The Python tests, frontend JS syntax, workflow YAML and source checks can be run in non-Windows environments. Compilation of the .NET agent/service and Inno Setup still requires the GitHub Windows runner and real install/update tests.
