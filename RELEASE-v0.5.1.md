# HomeWatch v0.5.1 — Faster transfers and optional Defender configuration

This is a Windows agent performance release based on **v0.5.0a**, retaining the working server schema, NAS browser, household catalogue and the three-file unattended HomeWatch updater format.

## Faster installer staging

- Multi-file packages: SHA-256 is now calculated **during copying** from the SMB share instead of reading each newly downloaded file a second time. The redundant whole-package verification pass has been removed. Existing cached files are hashed once to verify they still match the parent-approved manifest.
- Single-file legacy catalogue entries: the same streaming-copy-and-hash approach replaces the separate disk-reading hash passes.
- Copy progress is updated approximately every 200 ms rather than after each 1 MiB chunk.
- The setup `.exe` is still independently checked immediately before running from the writable cache; the multi-gigabyte data archives are not reread. This reduces but does not eliminate the possibility of cache modification after verification.

**Hashing is an integrity check, not a malware scan.** These changes don't silently bypass Windows Defender, Windows UAC or SmartScreen.

## OPTIONAL: one-time Microsoft Defender exclusions

If you expressly accept the risks, the repository includes an administrator-only script:

`tools/Manage-DefenderExclusions.ps1`

It is **not** called by the Windows agent, HomeWatch privileged service, remote update pipeline or web UI. It must be deliberately executed on each Windows PC using an administrator PowerShell window. It targets only:

- `AppData\Local\HomeWatch\Packages` and `AppData\Local\HomeWatch\installers` for each existing non-system user profile on that PC.
- Exact local game-library paths you provide; do not supply the system drive root or `Program Files`.

Example for a PC whose games live under `D:\Games`:

```powershell
# First preview what would be changed (run in an elevated PowerShell session):
.\tools\Manage-DefenderExclusions.ps1 -Action Add -GameFolders 'D:\Games' -WhatIf

# Then explicitly approve the changes:
.\tools\Manage-DefenderExclusions.ps1 -Action Add -GameFolders 'D:\Games'

# Undo exactly these exclusions later:
.\tools\Manage-DefenderExclusions.ps1 -Action Remove -GameFolders 'D:\Games'
```

For multiple libraries use `-GameFolders 'C:\Games','D:\Games'`. If Windows PowerShell script execution is disabled by local policy, you can configure the same folders in **Windows Security → Virus & threat protection → Manage settings → Exclusions** manually rather than changing policy.

**Security trade-off:** All new files written into an excluded folder, including unexpected or malicious files, may evade real-time antivirus scanning. Hash verification does not make arbitrary content trustworthy, especially executable setup programs that launch other processes. Do not exclude the entire `%LOCALAPPDATA%`, your Downloads folder, Windows, Program Files, or a drive root. Windows Defender exclusions can be overridden by organisational device policies. Administrators remain responsible for each client.

## Deployment

1. Replace the repository with the full v0.5.1 source and push to `main`.
2. Check that the Windows GitHub Actions job succeeds.
3. Create/push numeric tag `v0.5.1`, and verify the release ZIP includes all three HomeWatch executables.
4. Use the HomeWatch dashboard **Update now** action to update Windows clients from 0.5.0. **No server database migration or new Compose mount is required.**
5. Separately, if wanted, run the script locally with administrator approval on the Windows PCs whose Defender settings you wish to modify. These Defender exclusions are **not applied by the HomeWatch update itself**.

## Testing and limitations

Python tests assert the transfer/hash flow and regression contracts, but the Windows C# application has not been compiled in this environment and real SMB/Defender performance needs to be measured on a Windows PC. A real installation should still be attempted first with a small approved package.
