# HomeWatch v0.5.5 — self-signed Authenticode verification fix

The GitHub-hosted Windows runner returned `UnknownError` from `Get-AuthenticodeSignature` for the self-signed publisher despite successful `signtool sign` runs. `UnknownError` must not be accepted as a proof of integrity.

## New verification

- SignTool signs and RFC3161-timestamps the executables and Inno installer as before.
- The workflow installs `osslsigncode` v2.6 on the ephemeral Windows runner.
- It creates a temporary **public-only** PEM certificate from the signing PFX in GitHub Actions secrets.
- `osslsigncode verify -CAfile ... -require-leaf-hash sha256:<certificate SHA-256> -in ...` verifies the Authenticode signature and PE file digest of each artifact against that exact signing identity. Invalid hashes or a different signer fail the job.
- The CI check ignores timestamp validation so it does not need an additional TSA trust bundle. SignTool still timestamps; actual Windows updater clients continue enforcing Windows Authenticode/WinVerifyTrust and the locally approved pinned certificate.
- `Get-AuthenticodeSignature` remains solely as a diagnostic status printed to the log, not a pass/fail criterion.
- GitHub-hosted runner Trusted Root certificates are not modified.

## Deployment

Push this workflow to `main` and run CI. If the previous tag is already on an older commit, rerun on `main` and publish a new numeric tag such as `v0.5.5.1`; a rerun of the previous tag won't pick up workflow changes.

For teams still testing only v0.5.4, use `homewatch-release-v0.5.4-osslsigncode-fix.yml` as `.github/workflows/release.yml` to retain that base version.

No new signing certificates/secrets, agent reinstall, server migration, or Windows trust import is needed for this CI-only fix.

**Validation limit:** Python/schema tests and YAML parsing are local; the Windows verification flow must be confirmed by GitHub Actions.
