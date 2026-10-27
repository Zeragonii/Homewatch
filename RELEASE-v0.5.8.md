# HomeWatch v0.5.8 — Exact-file elevation policy and UAC runtime worker

This release provides the **first, UAC-gated stage** of parent-approved elevation. It does **not** install a SYSTEM runtime service or enable password-free elevation.

## Parent controls

- **Applications → Household software library**: choose **Allow elevation (UAC)** for an individual published installer package. The flag applies to its exact approved manifest, not to all `.exe` files. A package whose digest changes loses the flag.
- **Applications → Active approvals**: choose **Allow elevation (UAC)** for a specific approved game's executable SHA-256. The rule is reusable across devices only when the exact binary (including filename) matches and ordinary HomeWatch launch approval also exists for that device.
- Each flag can be reversed. Neither choice changes Windows User Account Control settings.

## Windows client

- Normal apps and installers run exactly as before.
- If a parent's exact-file elevation flag is enabled, **HomeWatchAgent.exe** launches a separate `--elevated-runtime` process using the Windows `runas` UAC verb. Windows prompts for administrator approval or credentials.
- The elevated runtime process rereads parent policy from HomeWatch under the device's existing API credentials; it does not accept arbitrary user-supplied command lines for games.
- The game worker resolves the approved executable pathname from the server and verifies its size and SHA-256 on disk before launching it.
- The installer worker requires a full NAS-generated package manifest, verifies every staged file immediately before elevation, and executes its specified setup EXE interactively. Legacy manual single-file packages cannot use this broker.
- The on-demand worker stays alive until its child process exits. While the launcher itself stays open, it tracks the worker so rapid clicks cannot spawn duplicate elevated instances. Force-close of an elevated game also goes through a separate UAC-approved runtime action.
- The **existing updater service remains unchanged**; signed update ZIPs still contain exactly three executables. There is no new independently installed SYSTEM service.

## Important security and usability limits

**This release is not password-free elevation.** It intentionally retains Windows UAC because a protected unattended runtime service needs separate service credentials, secure named-pipe authorisation, protected immutable package staging, session-aware process creation and explicit signer/policy verification. Those are not substituted with a command-execution endpoint in the existing SYSTEM updater.

When a standard Windows user supplies credentials for a *different* administrator account, elevated programs can run under that administrator's user profile. Games may consequently store settings and saves under a different profile. Test with a disposable game first.

The temporary installer cache is writable by the original user; the broker therefore **must** rehash all the package files before executing the approved installer. This extra local read is intentional and applies only to approved elevated installs.

If the launcher is restarted while an elevated game is running, its on-demand worker tracking may be lost until ordinary process detection can recognise the game. Stopping an elevated process requires UAC again.

Use HTTPS for the HomeWatch server if possible, keep your admin login and machine credentials protected, and avoid publishing unvetted programs. A SHA-256 hash identifies a binary; it does not make its code safe.

## Upgrade procedure

1. Deploy the updated server image to Portainer (this adds an automatically created grants table and one boolean column).
2. Publish the signed Windows release with Git tag `v0.5.8`.
3. Update one test client through the existing signed updater first; verify v0.5.7's reminder functionality still works.
4. Enable installer elevation for a small package in the Web UI, install on that test client and approve the Windows UAC prompt.
5. Approve its installed game's executable normally, enable the separate **Allow elevation (UAC)** control and test Play/Stop.
6. Only after successful client testing should you roll out the new feature across your other Windows PCs.

## Verification performed in development

Server API, regression and source tests; PostgreSQL DDL compilation; JavaScript syntax and Actions YAML parsing. **Windows compilation, UAC behaviour and real installation are not verified** until GitHub Actions and a Windows test machine are used.
