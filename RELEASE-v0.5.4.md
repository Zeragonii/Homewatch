# HomeWatch v0.5.4 — Signing certificates and authenticated updater trust

## Features

- **Applications → HomeWatch signing certificate** accepts a *public* PEM/DER X.509 `.cer` file only. Validates self-signature, current validity, non-CA and code-signing EKU. Rejects PFX/private keys. Stores public DER in PostgreSQL; no additional volume needed.
- Logged-in parent admin can see/copy **SHA-256 of the DER X.509 certificate** (64 hex characters). This is **not** Windows' usual SHA-1 certificate thumbprint.
- Enrolled Windows clients can fetch that public certificate over their authenticated `/agent/signing-certificate` endpoint. The private signing key **never goes through HomeWatch**.
- **HomeWatch Launcher → Performance settings (admin) → Signing certificate...** fetches it, validates it, shows its identity and requires the parent to paste the matching SHA-256 shown in the parent web UI. Windows UAC and explicit confirmation are required. Then it installs the public cert into LocalMachine `Root` and `TrustedPublisher` and writes its SHA-256 fingerprint to an administrator-protected file `C:\Program Files\HomeWatch\.updates\trusted-publisher.sha256`.
- The SYSTEM updater checks the Windows **WinVerifyTrust/Authenti­code signature** of **all three update executables**, validates certificate trust and compares each signer certificate DER SHA-256 against the **locally pinned** certificate. The apply helper checks again immediately before replacement. A web administrator changing the server certificate does not silently change an existing client's trusted publisher.
- GitHub Actions now requires the two signing secrets for **tagged releases**. It signs and verifies the three EXEs and signed Inno installer before publishing. Untagged CI builds can still run without signing secrets (but cannot be safely deployed to signer-enforced clients).

## First-time rollout order (important)

1. Generate a **dedicated HomeWatch self-signed code-signing certificate** on a trusted PC using PowerShell `New-SelfSignedCertificate -Type CodeSigningCert`, export **private** `HomeWatch-Signing.pfx` and **public** `HomeWatch-Public.cer`. **Back up the private key and password securely**.
2. Set repo **Actions secrets** `HOMEWATCH_SIGNING_PFX_BASE64` (base64 encoded `.pfx`) and `HOMEWATCH_SIGNING_PFX_PASSWORD` (PFX password); keep the PFX and password out of the repository.
3. Upload **only the public `.cer`** in Applications → HomeWatch signing certificate. Copy its SHA-256 fingerprint into a trusted record.
4. Push `main`, verify GitHub Actions signs the artifacts. Tag **`v0.5.4`** and verify the signed Windows release is published:

   ```powershell
   git tag v0.5.4
   git push origin v0.5.4
   ```

5. Use the existing v0.5.3 updater to install v0.5.4 (the old service does not yet enforce the pin). The HomeWatch server should be redeployed with the certificate upload feature first.
6. **On each PC**, open HomeWatch Launcher → Performance settings (admin). Enter UAC credentials and choose **Signing certificate...**. Click **Fetch public certificate**, paste the full SHA-256 from the web UI (independent confirmation), verify subject and expiry, click **Approve & install** and confirm. You need to do this once per machine.
7. Test with a **later** signed version/release to confirm signature enforcement. You can inspect the service status in HomeWatch's dashboard or `<install>\.updates\updater.log`.

**Critical:** Once v0.5.4 is installed, the updater will **refuse all later updates** until its local administrator trust pin exists, even if the ZIP hash matches. Do **not** publish an unsigned `v0.5.5` and expect it to update. Tag builds without configured signing secrets fail instead of producing an unsigned release.

## Trust and recovery

- This adds the self-signed *leaf* to Windows' machine-wide Root and TrustedPublisher stores. Anyone holding the private key can sign code trusted under this publisher. Protect the PFX; use it only for HomeWatch.
- The server cannot silently install/rotate a machine's trusted publisher. A different cert on the server causes a mismatch; the local admin UI refuses replacement of an already pinned certificate. Certificate rotation or recovery requires deliberate local administrator intervention.
- WebUI certificate deletion does **not** untrust/revoke certificates on PCs. The web UI only distributes public material.
- HomeWatch does not disable Defender or SmartScreen; signing does not guarantee either will stop identifying suspicious software.
- Existing device identity, database tables, and game installations remain unchanged; the new `signing_certificate` table is created automatically.
- Avoid changing `SESSION_SECRET` inadvertently because it also protects other settings. Certificate bytes are not secrets.
- **Do not deploy only the server** and assume enforcement exists: signature validation lives in the upgraded Windows service.
- File verification happens on Windows using its native `WinVerifyTrust` API, followed by a strict DER certificate SHA-256 match to the locally pinned certificate. A checksum alone is insufficient.

## Validation

Run `PYTHONPATH=. pytest -q tests` and `node --check server/app/static/app.js`. Windows .NET/Inno/GHA signing and local UAC import require actual Windows tests; neither was compiled/tested by the Linux packaging environment.
