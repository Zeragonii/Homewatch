# HomeWatch v0.4.1 — Update recovery, diagnostics and My Apps shortcut

## Changes

- The updater no longer rejects normal self-contained .NET builds due to the old 50 MiB-per-EXE / 100 MiB-archive limits. Current bounded limits: 200 MiB per EXE and 350 MiB per archive, with 450 MiB total uncompressed limit.
- The Windows service checks releases automatically every 15 minutes and processes the existing server's **Update now** request marker within its 8-second poll.
- An update is **not** reported as successful just because a server command was dispatched. Service phases (`checking`, `downloading`, `installing`, `installed`, `current`, `failed`) and errors are written to an administrator-controlled location and forwarded by agent heartbeat to the dashboard.
- Service stages updates and backs up original binaries before replacement, with attempted rollback if copying or service restart fails. This does not yet provide a full transactional health-check rollback.
- **HomeWatch My Apps** desktop shortcut defaults to on in the installer. On existing PCs, the upgraded agent also creates one per Windows user, once, if there is no shared desktop shortcut. Users can delete their shortcut without it being recreated indefinitely.
- GitHub Actions supports **optional** Authenticode signing for all three binaries and the setup EXE using a real publisher PFX. An unsigned build remains unsigned and may trigger Windows SmartScreen/reputation warnings.

## Important: recovery from the legacy updater

**Older updater services cannot update themselves if they reject the release ZIP before running the installer.** The size limits and diagnostics are inside the privileged service, so a machine stuck on 0.3.10 or 0.4.0 may need **one supervised installer upgrade** to 0.4.1 before later remote updates work.

Do not uninstall the working copy or delete `C:\ProgramData\HomeWatch\config.json`; it preserves the enrolled device identity.

When the v0.4.1 GitHub Windows build succeeds, download the official release installer on a trusted admin PC. Verify its published SHA-256 checksum. If Windows reports it as suspicious/malicious (rather than just unrecognized publisher), do not bypass the warning; investigate the detection. For unsigned SmartScreen reputation prompts, the supported solution is to obtain a trusted code-signing certificate rather than disable Windows protection. Install the release with administrator permission after verifying provenance.

### Check service state (PowerShell)

```powershell
Get-Service HomeWatchUpdaterService
Get-Content 'C:\Program Files\HomeWatch\.updates\status.json'
Get-Content 'C:\Program Files\HomeWatch\.updates\updater.log' -Tail 40
```

After v0.4.1 is running, press **Update now** in the dashboard and watch the device's **Updater** line. It reports the most recent service outcome on the next successful heartbeat (approximately 15 seconds). The command-history result means *queued*, not *installed*.

If the release is in a **private GitHub repository**, the LocalSystem service's anonymous GitHub API lookup will return 404. The current updater requires publicly readable release assets; private-repo support needs an authenticated or server-mediated updater pathway in a later release.

### Optional GitHub signing configuration

Provision an authentic publisher code-signing certificate (PFX) through a trusted certificate authority and configure GitHub Actions repository secrets:

- `HOMEWATCH_SIGNING_PFX_BASE64` — the Base64 encoding of the PFX **as a repository secret**, never committed to source.
- `HOMEWATCH_SIGNING_PFX_PASSWORD` — its passphrase, also a secret.

When both secrets exist, the Windows workflow signs the three EXEs **before** building the update ZIP, and the setup EXE **after** Inno Setup compilation, then publishes fresh SHA-256 hashes. Leave both unset if you do not have a legitimate certificate; do not generate a self-signed certificate and expect SmartScreen to trust it. A correctly signed file can still require time to earn application reputation.

**Security limitation:** The release SHA-256 is published beside the ZIP by the same GitHub account, so it detects corruption but does not independently authenticate the publisher. Signed-release enforcement by the privileged service remains future work. Do not treat this prototype as production-hardened endpoint management.

## Validation

- Python API tests, JS syntax and workflow YAML are checked in this build environment.
- The Windows/.NET build and Inno Setup installer require the GitHub Windows runner and a real-machine check. Verify service registration, automatic check, Update now, normal-user login, and application launch before deployment to the child's everyday PC.
