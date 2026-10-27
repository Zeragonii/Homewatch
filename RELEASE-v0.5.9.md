# HomeWatch v0.5.9 — Protected installer broker foundation (not unattended elevation)

## What this milestone does

- Introduces a separate, signed `HomeWatchInstallerBroker.exe` Windows service.
- Accepts only a numeric household catalogue ID through a local named pipe. No arbitrary path, arguments, program or command can be requested.
- Independently asks the server if this device is still permitted to install the exact package with `elevate_installer` enabled.
- Verifies a complete multi-file manifest while copying out of the standard user's cache into the service-owned protected `Program Files\HomeWatch\.installer-broker\staged` directory.
- Restricts stage requests to the logged-in Windows caller's registered profile, rejects reparse points, rejects traversals and checks file size and SHA-256.
- Maintains a service-side audit log and sends the protected staging location back to the Launcher.
- The existing UAC-elevated worker opens the interactive installer after a second setup-EXE hash check. The service never runs SYSTEM code on an interactive desktop.

## Security and prerequisites

**This version deliberately does not yet provide password-free elevated installation.** It is the protected staging and policy-verification milestone. Windows' UAC administrator prompt is still required for the interactive installer. Never interpret this release as a bypass of standard-user UAC.

The broker requires a trusted HTTPS URL for HomeWatch's API and a one-time local-administrator enrolment. The ordinary writable `C:\ProgramData\HomeWatch\config.json` cannot silently change the service's pinned server identity.

Because a fourth binary is introduced, the runtime broker must initially be installed by a **one-time elevated v0.5.9 Windows installer**. The existing three-executable signed updater ZIP stays unchanged and continues updating the current agent/service/updater. New broker versions will require an installer refresh until broker-specific update handling is available.

1. Deploy/publish the 0.5.9 server and Windows repo as usual, confirm all four Windows binaries and installer are signed.
2. Perform a one-time local administrator installation of the new Windows installer on a test PC.
3. Confirm your HomeWatch server is accessed via valid HTTPS.
4. In Launcher → Performance settings → Installer broker setup, approve local UAC enrolment. The broker copies the enrolled device identity to protected configuration and tests server connectivity.
5. Publish and approve a small multi-file installer from the Web UI.
6. On a standard Windows account, click Install and verify staging succeeds. Windows will still require local UAC credentials for the interactive setup.

### Known limitations

- New protected staging uses additional local disk space (one ordinary cache and one protected copy). Cleanup/quotas are future broker work.
- Initial stage progress is not streamed into the GUI. For large packages the UI may show only a preparation message until the broker responds.
- Fresh v0.5.9 signed releases still require real Windows compilation / UAC / SMB / IPC testing on your clients.
- For password-free *interactive* installation, a safe, separately provisioned administrator execution identity and desktop-session isolation are still required; we have not implemented that in this phase.
