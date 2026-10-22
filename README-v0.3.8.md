# HomeWatch v0.3.8 — machine-wide updater service (first implementation)

Runs `HomeWatchUpdaterService` as LocalSystem with the interactive tray agent
still starting for each signed-in user via HKLM Run. Only the service can write
into Program Files. A normal child account never needs administrator credentials
for **subsequent** agent updates.

## Security boundaries

- The service checks only the hard-coded public `Zeragonii/Homewatch` latest non-prerelease GitHub release.
- HTTPS plus the published release `.sha256` verifies archive integrity. This is **not publisher code signing**; compromise of release publishing credentials remains a risk. Implement mandatory Authenticode verification before considering it production-hardened.
- The archive must contain exactly three known executables; no arbitrary filenames or paths are allowed.
- Ordinary users may write `C:\ProgramData\HomeWatch\check-update.request` to request a check, **not** choose what runs.
- `C:\Program Files\HomeWatch\.updates` inherits protected installation ACLs and must not be user-writable.
- Shared `config.json` still has the pre-existing user-modifiable permissions and should eventually move device secrets behind the service.

## Upgrade notes

You must run this **one-time v0.3.8 installer as administrator** to install the Windows service. After that, the service can maintain subsequent versions in Program Files without prompting for admin login.

On service update, responsive running agents receive a restart marker and launch a delayed restart under the same user. Non-responsive agents may be terminated and then require signing out/back in.

### Limits

This is source code, not yet validated on Windows; Github Actions must compile it. Do not roll it out to every device until you have tested on a spare Windows installation. Releasing from a private repo is not supported by this first service version (it only supports public GitHub Releases). Initial installation or repair still requires elevation.
