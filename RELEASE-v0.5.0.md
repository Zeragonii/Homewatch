# HomeWatch v0.5.0 — Remote NAS installer library & interactive installation

**Purpose:** Publish a complete, approved Windows installer folder from a NAS, without mounting that NAS directory into the Docker container. Windows clients stage and verify the package, run the normal interactive installer, and locate the installed game for parental launch approval.

## What's new

- **NAS share configuration inside the web UI.** Under Applications → NAS installer source, set the share UNC path (`\\192.168.1.250\installers`), an optional read-only username and password, and the game installation folders you want the client to check. HomeWatch uses a Python SMB2/SMB3 client to browse the remote share. **Do not add a bind mount to Compose.**
- **Automatic package hashing and a remote file browser.** Click **Publish an app → Browse NAS**, navigate to a game's folder and select its setup EXE. HomeWatch recursively hashes up to **4,000 files** and **300 GB**; approval is for the complete folder manifest, including `.bin` parts and subdirectories. The publish dialog shows hashing progress and computes the SHA-256 manifest identifier for you.
- **SteamGridDB artwork selection.** Publishing a new game opens the existing artwork editor with a prefilled game-name search; you choose a portrait poster. `STEAMGRIDDB_API_KEY` is required for artwork, but not for the installer catalogue itself.
- **Native Windows folder staging.** Enrolled clients receive the approved share UNC path, package-relative file list, sizes and hashes. They copy the *entire* package preserving folder structure under `%LOCALAPPDATA%\HomeWatch\Packages\<id>-<digest>`, verify each file, recheck the publication and launch its EXE **normally in the user's session**. A cancel option is provided during copying. Windows network credentials are not supplied by the HomeWatch server; the signed-in Windows user must already have read access to the SMB share.
- **Installer and game discovery.** After the interactive installer exits successfully, HomeWatch checks new Windows uninstall registry locations plus configured Windows game folders. The child confirms a detected executable or browses to it. A fallback **Locate installed game** button is available when installation completed but the executable was not selected. Launching the installed executable still requires the existing parent approval mechanism.
- **Per-device installation records.** The server retains device-specific installed paths, executable fingerprints, approval requests and installation states. Admins can see installation activity in Applications.
- **Optional cleanup.** After registering an installed executable, the client offers to delete the staged *installer files*, leaving the installed game untouched.
- **Backward compatibility.** Existing v0.4.5 single-file entries are still recognised and use the legacy install path. The privileged HomeWatch updater, fixed three-EXE update ZIP and device enrolment are unchanged.

## Deploy v0.5.0

1. Back up the HomeWatch PostgreSQL volume and your existing server environment settings. **Do not reset** the database or enrolment data.
2. Push the contents of this repository to `main`. Confirm GitHub Actions **server-tests** and **windows-agent** jobs both succeed. The agent build was not compiled in the Linux development environment; Windows Actions must verify it.
3. Redeploy the `ghcr.io/zeragonii/homewatch-server:main` container in Portainer after the fresh image is published. Keep the existing screenshot and artwork volumes; no NAS bind mount is required.
4. Open **Applications → NAS installer source**. Set:
   - Share: `\\192.168.1.250\installers` (adjust if needed)
   - Username/password for a *read-only NAS account*; these are stored in encrypted form in PostgreSQL, keyed by `SESSION_SECRET`. Keep that secret long, strong, unchanged, and backed up. The web API never returns the password.
   - Game scan folders such as `C:\Games;D:\Games` (applied to each Windows client where those paths exist).
5. Click **Save and test share**. The server's Docker host must be able to reach NAS port **445/TCP**. No SMB mounting is necessary. Verify the chosen Windows users can **independently read** the same UNC location; credentials entered in the HomeWatch web UI are **not** forwarded to clients.
6. Click **Publish an app** → **Browse NAS**, select the game's directory and **setup.exe**, let hashing complete and save. Select matching SteamGridDB artwork when prompted. The game appears on every enrolled client's launcher.
7. Once both build jobs succeed, tag the release:
   ```powershell
   git tag v0.5.0
   git push origin v0.5.0
   ```
   Use the existing **Update now** function to upgrade the clients. Do not publish a tag until the build is green.
8. On a *test* Windows client, select the game, click **Install**, verify package copying, run its interactive wizard, confirm or browse to the installed game, approve the resulting executable in Applications, and verify **Play** works.

## Boundaries and caveats

- **No UAC bypass/elevation**: Installers execute with the user's normal Windows token, and UAC/administrator credential prompts remain. Interactive installers may need the parent present. The HomeWatch **SYSTEM updater does not execute game installers**.
- Both the server (for browsing/hashing) and the logged-in Windows account (for copying) require share access. A Docker container can use remote SMB **without mounting** the NAS; an ordinary Windows child account may need a separate NAS read-only session configured in Windows.
- Hashing and browsing jobs currently live in a **single server process**. Restarting the container during a scan requires restarting that scan. Do not run multiple Uvicorn workers with this initial implementation.
- An installer process can spawn a separate setup process and exit early. The detection step is best effort. If needed, complete setup and use **Locate installed game** to choose the actual game's EXE manually.
- Installation success is not inferred solely from the setup exit code; a known executable must be confirmed. Windows uninstall registry values are inconsistent across different installers. For a game installed to a nonstandard directory, choose its EXE in the file dialog.
- Editing an approved game package after publication changes its hashes; it will be rejected by the client until an updated manifest is published. Existing previously installed games aren't uninstalled by unpublishing the installer.
- Staging can require roughly the full package size of additional free storage. Minimum 1 GB free-space buffer is enforced. Partial staging can be reused once files verify, but this is not a resumable chunk downloader and cancellation can leave incomplete cached data.
- Development checks: 38 Python tests, JavaScript syntax and workflow YAML. **Real SMB negotiation, Windows compilation and interactive Windows installation remain to be verified on your infrastructure.**
