# HomeWatch v0.5.9c — broker IPC reliability hotfix

## Problem

Windows Launcher could show **Installation issue: Cannot access a closed pipe** when `HomeWatchInstallerBroker` terminated the named-pipe exchange without a readable result. In v0.5.9b the service could reject malformed requests or fail Windows caller impersonation before its reply writer was created. The client had no meaningful diagnostics.

## Changes

- Broker opens its response writer as soon as the named pipe connects; it catches identity, policy, and staging errors and attempts a bounded `ERROR` reply.
- Broker records the specific failing phase and exception category in the existing audit log, without logging device tokens.
- Launcher keeps read/write wrappers from closing the shared pipe and shows an actionable service/log hint on disconnect.
- A 20-second deadline applies to incomplete requests; the potentially long staging operation is not arbitrarily time-limited by the IPC read timeout.
- **No change to parent-controlled elevation policy, privileged executable selection, or the existing signed updater.**

## Deploy (important)

**The separate broker executable is NOT contained in the three-EXE automatic updater archive.** To get the service-side diagnostics and fix on a machine, publish `v0.5.9.3` and run the signed `HomeWatch-Agent-Setup-win-x64.exe` installer **once as administrator on that machine**. It stops/replaces/restarts the service and retains the protected broker enrolment configuration and `C:\ProgramData\HomeWatch\config.json`. Do not uninstall/delete the data. The agent alone can update automatically, but without an updated service the error may persist.

## Diagnostics

Open PowerShell as administrator:

```powershell
Get-Service HomeWatchInstallerBroker
Get-Content 'C:\Program Files\HomeWatch\.installer-broker\audit.log' -Tail 40
```

If the service is not Running, start it and look for service errors in Windows Event Viewer. Retry installing a small approved package. The audit file may explain the failure in the existing v0.5.9b release even before upgrading.

This is a named-pipe failure-handling patch, not a claim that the original root cause has been established without the on-device broker log. The installer still needs UAC for interactive administrator execution.
