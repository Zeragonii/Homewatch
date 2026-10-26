# HomeWatch v0.4.0 — My Apps and Application Approvals

## Summary

A first application-management workflow, built on the existing HomeWatch agent and server:

**Windows My Apps → Request → Parent review → Approved launch**

This is a **normal-user application launcher and parent-approved catalogue**, **not an administrator privilege bypass**. Applications that require UAC elevation still require administrator consent or separate narrowly scoped remediation. Approving a program in HomeWatch does not prevent a child from opening it directly from Explorer; the allowlist currently governs the *HomeWatch launcher*, not Windows execution policy.

### Child's Windows experience

- Double-click the HomeWatch tray icon, choose **My apps & requests** from its menu, or use the **HomeWatch My Apps** Start Menu shortcut provided by the v0.4 installer.
- Pick an installed `.exe` and optionally explain why they want it.
- The client calculates the binary SHA-256 and uses Windows `Get-AuthenticodeSignature` to obtain validation status, publisher, certificate subject/issuer and thumbprint.
- Requests show as **Waiting**, **Approved**, **Denied** or **Approval removed**.
- On approved launch, the local file is hashed and checked again, the server confirms a still-applicable approval, and the program starts **as the current Windows user** without passing arbitrary arguments.
- The portal refreshes automatically every 12 seconds, or with **Refresh**.

### Parent's browser experience

- Compact **Applications** menu with pending-request badge.
- Each request includes child/device, program path, hash, publisher, certificate details and optional child comment.
- Approve for **this device**, **all devices of the child**, or **entire family**. Global scope is offered only for binaries with a client-reported valid signature and a nonempty signer thumbprint.
- Deny, revoke, inspect request history and see launch outcomes.
- A global approval is for the **same binary version** (SHA-256, filename and signature fingerprint), not arbitrary files from the same publisher. Different versions require re-approval.

## Installation / upgrade

1. Update the repo and push to `main` so the GHCR server image is built.
2. Pull `ghcr.io/zeragonii/homewatch-server:main` in Portainer and redeploy the stack. PostgreSQL/SQLite receives **three new tables** automatically via SQLAlchemy `create_all`; existing HomeWatch records are untouched.
3. After a successful Windows build, tag `v0.4.0` and push the tag to produce a GitHub Release.
4. The existing v0.3.10 updater service is compatible with the v0.4.0 update ZIP: it contains the **same three EXEs** with no extra archive members.
5. Open **My apps & requests** from the agent's tray menu. The Start Menu shortcut is created by the v0.4.0 **installer** (an over-the-air binary-only update doesn't create installer shortcuts). Re-run the setup EXE once if you want the shortcut immediately.

## Suggested test

1. On the child's PC, request a benign `.exe` such as an ordinary user-level game launcher.
2. In **Applications**, approve it for the current device.
3. Refresh My Apps and try **Play / Open**. The launch should be visible in admin history.
4. Replace the executable or test a different binary with the same filename: server verification should reject it.
5. Revoke the approval; the launcher should stop offering a launch after its next refresh.
6. Request the identical signed executable from a second PC after giving it **global** approval: it should be auto-approved.
7. Verify that the ordinary heartbeat, messages, screenshots and updater still work.

## Security boundaries and limitations

- This version **does not elevate programs** and does not replace Windows UAC. The SYSTEM updater service remains restricted to fixed HomeWatch releases and has no new application-launch command.
- File-signature details originate from the requesting computer. The server doesn't possess the binary and **cannot independently authenticate** those client-reported claims. The client re-checks locally before launch. Do not use this data for privileged execution without a separately audited elevated broker, protected credentials, local file-access checks, publisher verification, and code-signing trust/rollback improvements.
- Local device credentials are currently stored in a ProgramData directory writable by normal users from the prior architecture. This is not a tamper-proof security boundary. Harden these credentials before implementing privileged launch/installation features.
- This does not block apps launched outside the HomeWatch portal.
- Browser admin account is the existing HomeWatch administrator; no separate role/user framework yet.
- Signature metadata collection requires Windows PowerShell with `Get-AuthenticodeSignature`, ordinarily present on Windows 10/11.
- The compiled .NET GUI and installer must still be validated on a Windows runner. Python API tests and browser-JS syntax checks do not prove Windows UI behaviour.

## Verification performed here

- Existing backend smoke tests + new approval, scope, revocation, mutation, authentication and audit regression tests.
- Front-end JS syntax check (`node --check`).
- Python compile check and GitHub Actions YAML parse.
- **Not performed:** Windows `.NET` compilation, Windows desktop layout/DPI interaction, end-to-end privileged service update from a real machine.
