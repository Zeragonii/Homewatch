# HomeWatch v0.5.2 — Shared game launch paths

## Why
Some installers create a deeply nested game executable. After the first household installation is located, HomeWatch can learn the relative path once and reuse it on later PCs.

## Workflow
1. On the first PC, install the household package and locate the game's executable as normal.
2. If HomeWatch can determine the installation root from a Windows uninstall record or configured game search folders, it sends a *suggested relative executable path* to the server. Example: `Content\Binaries\Win64\Game.exe` (not `D:\Games\...`).
3. The parent opens **Applications → Household software library → Shared game launch paths**, reviews the suggestion and chooses **Use for future installs** (or Reject).
4. A future installation of that same package on another PC uses the approved relative executable path under that PC's discovered installation root. It only auto-selects a path if exactly one existing EXE matches; otherwise it falls back to the ordinary selection workflow.
5. The chosen executable still goes through the existing SHA-256/signature-based parent launch approval. Approval of the path does **not** bypass that requirement.
6. The parent can use **Set path** on a catalogue entry to type or correct the relative EXE path manually, or **Clear** it. Changing the underlying installer package invalidates the approved launch path.

## Deployment
- This is a server and Windows agent update, not a database reset.
- The existing `household_catalogue` table receives one additional `launch_relative_path` column during server startup; a separate suggestion table is created automatically. Back up PostgreSQL before major upgrades.
- Push the full repository to `main` and wait for the Docker build. Redeploy the server image in Portainer.
- Publish Windows GitHub Release tag `v0.5.2`, then use the dashboard's existing update action to update clients.
- All three Windows update executables retain their existing packaging names and updater pipeline.

## Defender policy
This release does **not** add remotely initiated Defender exclusions or exclude HomeWatch's own binary directory. The earlier local administrator-managed exclusion script remains unchanged. For false positives affecting HomeWatch binaries, consider signing the executable/installer and reporting incorrect detections to Microsoft Security Intelligence instead of allowing a remote monitoring service to weaken malware scanning for itself.

## Limitations and safety
- A reliable relative path requires a known game installation root. If a game is installed outside the configured search folders and doesn't register an uninstall location, the parent can enter the relative path manually.
- A relative hint depends on the game's folder structure remaining consistent. If multiple matches are found or the file isn't present, automatic selection is skipped.
- A user's installed executable can differ from the original installer; the approved *path* never itself authorises execution.
- Windows compilation, deployment on real Windows hardware and actual game installation still require validation through GitHub Actions and a test client.
