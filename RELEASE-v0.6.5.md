# HomeWatch v0.6.5 — Fast ZIP-chunk game distribution

**Base:** v0.6.4.1 (which includes all recovered v0.6.3.2 broker, updater and wellbeing work).

## Why

Portable deployment previously opened every game file separately over SMB on the child's PC. Games with thousands of small files suffered severe SMB round-trip overhead. The four parallel hash workers added in v0.6.4.1 only sped up *publication*, not Windows copying.

## What's new

- During **Publish / Rescan** of a **Ready-to-play game folder**, HomeWatch still generates the existing SHA-256 manifest, then creates immutable **uncompressed ZIP (STORE) parts** at the root of the configured NAS share: `.homewatch-packages/<manifest-digest>/part-0001.zip`, etc.
- The target chunk size is **1 GiB**; chunks contain whole files, so a *single* file larger than 1 GiB gets its own larger ZIP64 chunk. No compression time or decompression CPU penalty. Files are never copied into a different source directory.
- The server verifies SHA-256 *while preparing* ZIP entries to detect changes between scan and package creation. An unchanged rescan reuses existing chunks.
- The Windows client opens ZIP parts **directly over SMB** and stream-extracts directly into `.homewatch-staging` on the game drive. **No local ZIP download or duplicate full-sized game copy.** It checks package entry names, sizes, chunk order, final inventory and the **game executable's SHA-256** before promotion. Other assets are not SHA-256-verified on the child during deployment (per request); this trades some defense against tampered NAS content for speed.
- Interrupted transfers can be resumed by skipping chunks whose extracted files are already present with expected sizes and whose EXE hash matches. Files in the currently interrupted chunk may be re-extracted; archives aren't downloaded locally.
- Progress reports chunk count, MiB/s and percentages, including during extraction.
- New revisions intentionally re-download the entire game. The prior installed folder is retained under `.homewatch-backups`, preserving the existing v0.6.4 rollback mechanism. **No delta updates or chunk reuse across revisions.**
- Earlier portable entries published before v0.6.5 continue to use the legacy per-file SMB copy. Rescan and republish them to enable ZIP transport; ordinary existing installations remain in place.
- Traditional installer packages, the password-free installer broker, its separate signed updater, application approval, wellbeing reminders and other v0.6.4.1 functionality are unchanged.

## NAS permissions and capacity

The NAS account configured in **Applications → NAS settings** needs **read access to the prepared game folders AND write access to `.homewatch-packages`** in the configured share root. Users' Windows accounts need **read access** to those ZIP parts over SMB. HomeWatch doesn't need to expose the web server to the internet.

The NAS retains both the original prepared game files and the ZIP distribution copy. Budget approximately an **extra 100% of each game size** on the NAS. ZIPs aren't automatically deleted when an old game revision is unpublished; inspect old archive directories and their published references before removing them. Temporary `.building-*` folders can indicate an interrupted/failed NAS packaging operation.

If a scan says the NAS cache cannot be created, review the server-side SMB credentials and share permissions. The original prepared folder is not changed.

## Publishing

1. Push the new full repository to GitHub and redeploy the HomeWatch server.
2. Allow write access to `.homewatch-packages` at your NAS share root for HomeWatch's configured SMB user.
3. In Web UI, open the **Ready-to-play game** entry, choose the source folder and launch EXE and **Scan** again. The scan now includes an initial hash pass and a one-off ZIP packaging pass; **wait for completion before publishing**.
4. Publish the new manifest/revision; update your child's Windows agent to v0.6.5 through the normal signed updater.
5. Click **Install** or **Update** on the child PC. The agent reads ZIP parts directly from NAS and stream-extracts into its existing game drive staging folder.
6. Confirm launch through the normal parent executable approval flow.

Note: v0.6.4.1 clients don't understand ZIP transport, so **deploy the updated Windows agent before installing a newly chunked package**.

## Git release

```powershell
git add .
git commit -m "v0.6.5"
git push
git tag v0.6.5
git push origin v0.6.5
```

## Verification

`PYTHONPATH=. pytest -q tests` — 141 tests passed, including uncompressed ZIP creation, cache reuse, changed-content detection and Windows source contracts. `node --check server/app/static/app.js`, Python syntax compilation, workflow YAML parsing and final ZIP integrity verified. The actual Windows .NET compilation and live SMB performance have **not** been tested locally; verify via GitHub Actions and one real game install.
