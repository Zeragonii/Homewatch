# HomeWatch v0.6.4 — Ready-to-play game deployment (based on v0.6.3.2)

This release is **built from the recovered v0.6.3.2 complete repository**, not the separate v0.6.0 ready-to-play experiment. Every part of the existing password-free interactive installer broker, its dedicated runner identity, protected staging and cleanup, the separately signed broker auto-update, client update trust, wellbeing reminders, and application approvals remains in place.

## Publish a prepared game

1. On the parent's computer, install the game into a dedicated folder and test it. Copy the **complete installed folder** to your NAS share. HomeWatch does not run or inspect game installers for portable packages.
2. In the HomeWatch parent Web UI open **Applications → Household Software Library → Publish an app**.
3. Set **Deployment method → Ready-to-play game folder** (default for new entries). Choose **Browse NAS**, navigate into the prepared game folder and select **Use this folder as game root**. Browse inside it, including nested subdirectories, and click the **actual game EXE**. Do not select the repack installer EXE.
4. Wait for the whole-directory SHA-256 manifest scan to complete, set the friendly name and publish. Artwork editing is unchanged; when the same catalogue entry is republished with a different package hash, the existing game poster is retained.
5. On the child's PC, choose the game in HomeWatch Launcher and press **Install**. The client must have **read access to the NAS SMB share** and **write access to an already-existing configured local game folder** such as `D:\Games`. The NAS credentials configured for server browsing are not distributed to clients.
6. The client copies every file once, calculating SHA-256 while writing it to `D:\Games\.homewatch-staging\<id>-<package-SHA>\`. Already verified files are reused after an interrupted transfer. When all members match, HomeWatch renames the staged folder into `D:\Games\<Game Name> [HW-<id>]\` without recopying it.
7. The main game EXE is submitted through the **existing parent-managed application approval flow**. The game is not automatically authorised to execute merely because the folder was published. After the parent approves its exact executable fingerprint, **Play/Stop** work as before.

No elevated installer, remote administrator identity, or HTTPS installer broker session is required for **portable game copies**. The HomeWatch server API retains whatever HTTP or private-HTTPS configuration you already use. The source game bytes move **directly over SMB** from the NAS to the child PC, not through the HomeWatch web API or NPM.

## Revision updates and rollback

Re-publishing a prepared folder to the **same catalogue entry** creates a new immutable package digest and resets the shared game launch path to that revision's chosen EXE. A client with an older installed package sees **Update game** while its current approved version remains playable. Updates stage a **complete new revision**; they do not modify the current game directory in place. Upon successful verification the previous directory is moved to `D:\Games\.homewatch-backups\` and the new staging folder is promoted to the original installation path. The previous directory (including any portable saves or mods) is deliberately preserved. Copy save files manually if the game stores them inside its own directory.

The client does not implement changed-files-only delta updates yet. Each new revision copies all files into its own staging area, although interrupted transfers resume successfully. An existing game's executable may require **fresh approval** after the binary hash changes. Do not update while a game is running. Backups require additional free disk capacity and are not automatically pruned; manage them after verifying saves and gameplay.

## Legacy and privileged installations remain supported

- Traditional installer catalogue entries retain the existing NAS manifest downloader, unprivileged interactive wizard and **v0.6.3.2 password-free, parent-authorised installer broker**.
- The broker still requires its initial administrator provisioning and trusted HTTPS server enrolment **only when you use the privileged installer mode**. Portable games do not depend on those settings.
- Previous installer packages retain their existing `deployment_type='installer'` automatically through a PostgreSQL migration. Database/device enrolment and existing approvals are untouched.
- The three-file signed HomeWatch agent update archive and **separately signed broker executable update** remain unchanged in format. The source version is `0.6.4`, so use tag `v0.6.4`.
- The existing wellbeing reminder foreground fix, alerts, screen time, screenshots, software library artwork and installed-game launch protections remain in the codebase.

## Safety and limitations

- Directories must not contain junctions/reparse points; unsafe file paths, duplicate Windows filenames, missing entries or changed SHA-256 fingerprints prevent promotion. The source is limited to the read-only, parent-configured SMB share and a verified, approved manifest.
- Promotion is a same-volume directory rename, not a second full data copy. A power failure or filesystem problem may still require rerunning **Install**, which will verify/resume files. When the final folder already exists and matches the approved manifest, HomeWatch can recover its registration without recopying.
- HomeWatch does **not** automatically disable Windows Defender or install system-level game dependencies. Some prepared games might require VC++/DirectX or external launchers; those remain manual or legacy installer tasks.
- The previously deployed game remains in its own directory while an update transfers; if final promotion fails HomeWatch attempts to restore the original folder. If the backend becomes unavailable **after** successful promotion, rerun the installer to retry approval registration.
- This release was exercised through Python API regression tests, JavaScript syntax checking, workflow YAML parsing and source-based Windows checks. The Linux environment has **no Windows .NET SDK**, so the first actual C# compile and end-to-end SMB/NTFS test must happen through GitHub Actions and a Windows client.

## Deployment

Push the full repository to `main`, redeploy the HomeWatch server, then run the usual signed tag release:

```powershell
git tag v0.6.4
git push origin v0.6.4
```

Test first with one small, prepared game copied to the NAS. Verify the entire path: publish → copy → approve EXE → play → stop → uninstall. Test an interrupted installation before attempting a large transfer, and then test a second revision with a game that stores save files outside its installation directory.
