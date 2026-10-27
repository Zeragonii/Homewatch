# HomeWatch v0.6.0 — Parent-approved interactive administrator installers

## What changes

Previously, HomeWatch v0.5.9 staged approved packages in a protected folder and then launched the setup program via Windows UAC. Standard-user children still needed a parent's administrator password for every elevated installer.

**v0.6.0 implements an installation-only, password-free interactive broker.** An approved installer opens its normal wizard in the child's console Windows session under a separately provisioned local administrator account. Nothing runs through the signed update service, the child cannot supply an arbitrary executable path or arguments to the privileged broker, and game launch elevation remains unchanged (UAC-gated).

### Security boundary

- Parent Web UI must have **Allow elevation (UAC)** enabled for the exact household software catalogue item. This existing flag now selects broker-driven execution, rather than a UAC prompt.
- The launcher downloads the complete package directly from SMB, validating all file hashes against the server's manifest.
- The dedicated broker independently checks the current server approval over the previously pinned HTTPS connection and copies the complete package into `C:\Program Files\HomeWatch\.installer-broker\staged` with SHA-256 verification of every file. It rechecks server approval and the setup executable's hash immediately before launch.
- The broker receives **only a catalogue ID** for launching an installer. It will not accept executable paths, command lines, arbitrary arguments, or an administrator password through IPC.
- Caller identity is obtained through Windows named-pipe impersonation. The broker verifies the active Windows console session belongs to that caller.
- Administrator provisioning is **opt-in locally via UAC**. It creates `HWInstallRunner`, a dedicated local administrator with a random machine-encrypted password and ACL-protected credentials. The account is distinct from the service's SYSTEM identity and from the parent's regular Windows user.
- The interactive launcher grants the dedicated account access to the current desktop before asking the broker to start the wizard; the broker uses `CreateProcessAsUser` and manages the runner's Windows profile and exit status.
- Status and completion are returned via authenticated named-pipe polling and logged. Only one active privileged installer is allowed at a time.
- Broker credentials are deleted and the dedicated account is removed on uninstall.

**This is administrator-level delegation, NOT a sandbox.** A GUI installer may modify system files or run further programs. Some launchers and installers may create processes that outlive the setup executable. Use only parent-reviewed software. The dedicated administrator's per-user profile and registry are separate from the child's, so choose an *all users / machine-wide* game installation when possible. Some anti-cheat/game launchers will not work under this model.

## Installing and provisioning

1. Push repository contents and confirm the **Windows GitHub Actions workflow builds and signs all four EXEs**. Publish a signed release `v0.6.0`.
2. On the Windows PC, run **HomeWatch-Agent-Setup-win-x64.exe** as administrator. **Mandatory:** the normal three-executable updater ZIP does not update `HomeWatchInstallerBroker.exe`.
3. Verify `Get-Service HomeWatchInstallerBroker` displays `Running`. Keep the existing HTTPS server configuration and device enrolment.
4. From HomeWatch Launcher, open **Performance settings → Installer broker setup…** (administrator approval required).
5. Confirm the pinned HTTPS server/device, then explicitly confirm creation of the `HWInstallRunner` dedicated administrator account. If your previous broker enrolment was already present, running this step again adds only the account (without clearing the device).
6. In HomeWatch Web UI, choose a small, known-good household installer and enable **Allow elevation (UAC)** for its exact catalogue item.
7. Sign in to the child's **standard Windows account**, click **Install**, and confirm the normal interactive wizard opens **without a UAC password prompt**. Its session and administrator identity are intentionally different from the child's account.
8. Complete the installation. HomeWatch should record exit status and proceed to its existing game-executable discovery/approval process. If the installer prompts about scope, select *Install for all users*.
9. Turn off **Allow elevation** on the server and attempt a second installation; the broker should deny it.

### Diagnostics

```powershell
Get-Service HomeWatchInstallerBroker
Get-Content 'C:\Program Files\HomeWatch\.installer-broker\audit.log' -Tail 60
Get-LocalUser HWInstallRunner | Select-Object Name, Enabled
```

If `CreateProcessAsUser` fails, the broker log should contain a Win32 error code. In particular, session/desktop security policies vary; the Windows service must be running as SYSTEM and your daughter must be using the active console session. **Windows 11 build/desktop behaviour must be tested on real hardware; Windows compilation and graphical execution cannot be verified in the Linux development container.** Do not deploy unattended to multiple clients until a small installer works end to end.

When diagnosing, don't share `broker-config.json` or `runner-identity.json`: they contain device or encrypted local credentials. The audit log and the Win32 error code are sufficient.

## Deploy / version

```powershell
git tag v0.6.0
git push origin v0.6.0
```

The Postgres schema and server API have not changed. No database wipe or device re-enrolment is needed. The broker's fourth signed EXE requires an administrator-run installer **for this upgrade**. Game launch elevation is a separate future milestone.
