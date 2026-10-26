# HomeWatch v0.5.5 — GitHub Actions signing CI hotfix

This patch changes **only the GitHub Actions signing workflow** on top of v0.5.5. It retains the existing HomeWatch agent, server, installer and signing key version.

## Why

The previous release workflow imported the developer's self-signed code-signing certificate into `Cert:\CurrentUser\Root` on an unattended GitHub Actions Windows runner. Windows can show a confirmation dialog for this operation, leading to a job that appears to hang immediately after signing all three binaries.

## Fix

- **No runner root certificate import.** GitHub Actions need not alter the host's system trust store to confirm the exact embedded signer fingerprint.
- `Get-AuthenticodeSignature` checks for `Valid` or `NotTrusted` only (the latter is expected for an otherwise correctly signed private certificate not trusted by Windows on the ephemeral runner); missing or mismatched signers and all other statuses are rejected.
- Signer DER certificate is SHA-256 pinned to the certificate extracted from the PFX stored in Actions secrets, both for the three EXEs and the Inno Setup installer.
- Progress messages immediately before and after each verification, plus an **8-minute timeout** on the signing step.
- **HomeWatch clients are unchanged:** the privileged service still requires locally installed/pinned trust and a valid signature before applying updates.

## Use

If you are still trying to build v0.5.4, you can copy **only** `.github/workflows/release.yml` into your active v0.5.4 branch and restore that branch's version fallback (`0.5.4` instead of `0.5.5`). For v0.5.5, use this entire repository as the release candidate.

No new keys, GitHub secrets, machine cert setup, Docker change, or database migration are needed. Re-run the failed Windows build after pushing the workflow change. Actual Windows signing and signature status still require validation on GitHub Actions.
