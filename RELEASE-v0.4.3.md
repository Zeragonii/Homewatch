# HomeWatch v0.4.3 — Updater service path and installer repair

## Problem

A previous updater can fail with `Could not find file C:\Program Files\HomeWatch\HomeWatchService.exe` while handing off an update. This happens if the expected binary is absent, the service is registered at another path, or an installation was interrupted. It is NOT an approval or enrolment problem.

## Changes

- The privileged service checks the actual running executable path (`Environment.ProcessPath`) and reports an actionable repair error when it does not match the configured HomeWatch install directory.
- Manual agent update requests always write the marker rather than refusing when the agent's guessed service path does not exist. The service is the authority on its own installation status.
- The installer explicitly packages all three required executables and reconfigures the existing Windows service's `binPath`, instead of only configuring the service on first install.
- The per-user agent, device ID, account association, application approvals, metadata, and posters are unchanged.

## Diagnose before repairing (elevated PowerShell)

```powershell
Get-CimInstance Win32_Service -Filter "Name='HomeWatchUpdaterService'" | Select-Object Name, State, PathName
Get-ChildItem "$env:ProgramFiles\HomeWatch" -Filter 'HomeWatch*.exe' | Select-Object Name, Length
Get-Content "$env:ProgramFiles\HomeWatch\.updates\updater.log" -Tail 25 -ErrorAction SilentlyContinue
```

If HomeWatchService.exe is missing from the install directory, **no currently running updater can reliably repair itself**. Install the signed, verified HomeWatch setup EXE once under an administrator account after checking the certificate and release checksum. Do not bypass Windows Defender or SmartScreen to do so. A missing code-signing certificate needs resolving through trusted signing, not by switching off security.

**Do not clear or remove `C:\ProgramData\HomeWatch\config.json`, or delete the device on the server.** This file preserves enrolment. Inno Setup normally preserves this file during reinstall.

After installing, verify the three EXEs exist and the service path is correct. Then future versions may be tested through the remote update button. The Windows service and installer must be compiled and install-tested on a Windows runner; this ZIP has only been source/test validated here.
