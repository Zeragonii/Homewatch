# HomeWatch v0.4.5 — Household software library

## Purpose

Publish the same application installer to every enrolled Windows PC from the HomeWatch admin web UI. The existing per-device/child/global **application approval** system is unchanged. The new **household catalogue** is a software-distribution feature, not a Windows administrator-elevation bypass.

## Adding an installer from your NAS

1. Place a **standalone `.exe` installer** somewhere every child's Windows account can read, for example the NAS share `installers` on `192.168.1.250`.
2. Use a Windows UNC path such as:

   ```text
   \\192.168.1.250\installers\game1\game1install.exe
   ```

   The admin UI also accepts `192.168.1.250/installers/game1/game1install.exe` and converts it into the UNC form. If you serve packages over HTTP(S) instead, use a direct `.exe` download URL without credentials or query parameters.
3. From a Windows PC that can read that NAS share, open PowerShell and generate the fingerprint:

   ```powershell
   (Get-FileHash '\\192.168.1.250\installers\game1\game1install.exe' -Algorithm SHA256).Hash
   ```

4. Open HomeWatch web UI → **Applications** → **Household software library** → **Publish an app**. Enter friendly name, NAS installer path, SHA-256, and optional description. Leave **Available to every enrolled device** enabled.
5. Optionally select **Edit tile** and assign centrally cached SteamGridDB artwork.
6. Deploy the server update and remotely update the Windows agent to v0.4.5. The app appears on every enrolled device's HomeWatch Launcher grid with an **Install** action.

## What the Windows client does

- Retrieves the published list over its existing authenticated HomeWatch API.
- When a child chooses **Install**, gets the current catalogue entry again and requests confirmation.
- Copies/downloads the file into `%LOCALAPPDATA%\HomeWatch\installers\<sha256>` (8 GiB maximum). No HomeWatch device bearer token is forwarded to a NAS URL.
- Verifies SHA-256 before saving and again before running; rejects mismatches.
- Rechecks server-side publication status immediately before launching.
- Opens the verified executable under the **currently logged-in user's existing Windows privileges** and reports the attempt.

The Windows client must itself have **read permission** to the NAS share. The server running in Docker does not need a NAS mount; it only stores the path and SHA-256. The client retains downloaded installers under its own profile (delete that cache as desired).

**Limitations:** The initial release supports a single standalone `.exe` per catalogue entry, not installer folders with companion files or installers requiring command-line arguments. Running the installer is not proof installation finished. Apps needing administrator access may still trigger UAC and require parental approval. Installed program executables are still approved through the existing request/approval workflow; catalogue availability does **not** implicitly approve an unrelated installed `.exe`. No arbitrary executable is ever passed to HomeWatch's privileged updater service.

## Release and deployment

1. Push the repository to `main` and confirm both GitHub Actions Windows build and server image build succeed.
2. Pull/redeploy the server image in Portainer. The two new database tables are created automatically; do **not** delete your Postgres volume.
3. Create the release tag:

   ```powershell
   git tag v0.4.5
   git push origin v0.4.5
   ```

4. Once the release ZIP is available, use **Update now** to upgrade existing agents and verify the reported version changes to `0.4.5`.
5. Add a **test installer**, verify it shows up in the Windows Launcher on two devices, and confirm it downloads and opens without bypassing Windows UAC.

**Windows compile limitation:** Server tests, web JavaScript syntax, and workflow YAML are checked here; a Windows/GitHub Actions run is still required to compile the updated WinForms source.
