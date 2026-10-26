# HomeWatch v0.4.3 — Service repair and updater diagnostics

See [RELEASE-v0.4.3.md](RELEASE-v0.4.3.md) for verification and recovery.

# HomeWatch v0.4.2 — Poster-Grid My Apps Launcher

See [RELEASE-v0.4.2.md](RELEASE-v0.4.2.md) for poster art configuration, Portainer storage, desktop UI changes, deployment and updater test steps.

# HomeWatch v0.4.1 — Update Reliability

See [RELEASE-v0.4.1.md](RELEASE-v0.4.1.md) for the updater-size fix, desktop shortcut behaviour, diagnostic status and optional Windows code-signing configuration. The legacy updater may need one verified, administrator-approved installer upgrade to enable the repaired silent-update path. Avoid disabling Windows security features.

---

# HomeWatch v0.2.6.5

A self-hosted, multi-device parental monitoring MVP with a Windows tray agent.

## v0.1.5 highlights

- Proper Windows installer (`HomeWatch-Agent-Setup-win-x64.exe`)
- Per-user installation under `%LOCALAPPDATA%\Programs\HomeWatch`
- Existing enrolment/config survives reinstalling or moving from the portable build
- Automatic agent self-updates from the latest normal GitHub Release
- Agent version comes from the actual tagged build rather than a hard-coded `0.1.0`
- SHA-256 verification before any downloaded update is installed
- Logged-in Windows user reported with each heartbeat and shown in the dashboard
- Agent update/error log at `%LOCALAPPDATA%\HomeWatch\agent.log`

## Architecture

```text
Windows tray agent  --HTTPS-->  HomeWatch API  <--> PostgreSQL
      |                               |
 foreground app                  Parent web UI
 logged-in user                  enrol / stats
 screenshots                     commands
 messages                        release authority
 updater
```

The agent installation has its own `installation_id`, but the **logical device UUID is created by the server only when an admin approves enrolment**. A reinstall can therefore be bound back onto an existing device and keep the same history.

## Server quick start

For Portainer/GHCR deployments use the published server image:

```yaml
services:
  db:
    image: postgres:17-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: homewatch
      POSTGRES_USER: homewatch
      POSTGRES_PASSWORD: CHANGE_ME
    volumes:
      - homewatch_db:/var/lib/postgresql/data

  app:
    image: ghcr.io/zeragonii/homewatch-server:main
    restart: unless-stopped
    depends_on:
      - db
    environment:
      ADMIN_USERNAME: admin
      ADMIN_PASSWORD: CHANGE_ME
      SESSION_SECRET: CHANGE_ME_LONG_RANDOM_VALUE
      DATABASE_URL: postgresql+psycopg://homewatch:CHANGE_ME@db:5432/homewatch
      SCREENSHOT_DIR: /data/screenshots
      APP_TITLE: HomeWatch
      AGENT_RELEASE_REPO: Zeragonii/Homewatch
      # GITHUB_TOKEN: github_pat_xxx   # only required for a private GitHub repo
    ports:
      - "8090:8000"
    volumes:
      - homewatch_screenshots:/data/screenshots

volumes:
  homewatch_db:
  homewatch_screenshots:
```

For real use outside your LAN, put the service behind HTTPS. Do not expose the agent API over plain HTTP on the public Internet.

## Installing the Windows agent

Create a tagged release, for example:

```bash
git tag v0.1.5
git push origin v0.1.5
```

GitHub Actions builds and attaches:

- `HomeWatch-Agent-Setup-win-x64.exe` — recommended first install
- `HomeWatch-Agent-Setup-win-x64.sha256`
- `HomeWatch-Agent-win-x64.zip` — update/portable package
- `HomeWatch-Agent-win-x64.sha256`

Run the setup EXE on the child's PC. It installs to:

```text
%LOCALAPPDATA%\Programs\HomeWatch
```

The agent asks only for the HomeWatch server URL, then appears under **Onboarding** for administrator approval.

Agent state remains in:

```text
%LOCALAPPDATA%\HomeWatch\config.json
```

That means installing a newer setup package does not create a new logical client or lose the existing enrolment.

## Automatic updates

The server checks the latest normal GitHub Release from `AGENT_RELEASE_REPO` (cached for five minutes). It looks specifically for:

```text
HomeWatch-Agent-win-x64.zip
HomeWatch-Agent-win-x64.sha256
```

An enrolled agent checks the server every 15 minutes. If the release version is newer than its running version it:

1. Downloads the ZIP.
2. Verifies the SHA-256 supplied by the server.
3. Copies `HomeWatchUpdater.exe` to a temporary location.
4. Exits the running agent.
5. Replaces the installed files in-place.
6. Restarts the agent.

Because the installer is per-user under LocalAppData, normal self-updates do not need an administrator/UAC prompt.

A release manually promoted through `/api/releases` still takes precedence over GitHub auto-discovery, leaving room for future stable/beta/pinned update controls.

## Private GitHub repositories

If the repository/releases are private, set `GITHUB_TOKEN` on the server container to a token that can read the repository. Public repositories do not need one.

## Safety / privacy defaults

HomeWatch is intentionally visible parental-management software rather than covert surveillance:

- tray icon is visible;
- enrolment requires admin approval;
- screenshots are on-demand, not continuously archived;
- no keylogging, microphone capture, credential capture, or stealth/persistence tricks;
- credentials are revocable and per-device.

## Current limitations

- no WebRTC/live video yet;
- installer and binaries are not Authenticode code-signed yet, so Windows SmartScreen may warn on first install;
- no role-based parent accounts yet;
- screenshot storage is local filesystem storage on the server;
- self-update verifies hashes, but signed-package verification is a future hardening step.


## v0.1.6 manual update checks

The parent dashboard can queue an immediate update check for one device or all enrolled devices. Manual checks bypass the server's short GitHub release cache, while the normal 15-minute background check remains enabled. Offline agents keep the queued command and execute it when they next reconnect.


## v0.3 screen-time controls

The Screen time menu adds child-level policies shared across all enrolled devices for that child:

- weekday/weekend allowed-use windows;
- weekday/weekend daily allowances (0 means unlimited);
- configurable warning and grace periods;
- one-day +15/+30/+60 minute extensions;
- per-application limits by Windows executable name;
- automatic workstation locking when the overall policy is blocked;
- automatic closing of an application whose specific allowance is exhausted.

Set `FAMILY_TIMEZONE` (default `Europe/London`) so schedules and day boundaries match the household. Screen-time enforcement requires the v0.3 agent; older agents continue monitoring but do not enforce these policies.

## v0.3.3 typed messaging

Parent messages now support three delivery modes:

- **Notify** — a normal dismissible Windows notification.
- **Question** — a styled HomeWatch dialog that requires a non-empty reply before it can close. The reply is returned to the server and shown in device command/message history.
- **Alert** — a styled HomeWatch dialog that requires explicit acknowledgement before it can close. The acknowledgement is returned to the server and shown in device command/message history.

Question and Alert windows run on their own UI thread, so the agent continues heartbeats, activity tracking, policy enforcement, and update checks while a response is pending.


## v0.3.4 message dialog polish

- Question and Alert dialogs explicitly force themselves foreground/topmost when shown.
- Dialog layout uses fixed table rows so message text, reply controls and action buttons cannot overlap at different DPI/font scaling.
- Long messages use a scrollable read-only message area.


## v0.3.5 reliable interactive replies

- Question replies and Alert acknowledgements use a dedicated authenticated response endpoint.
- Agent verifies the server accepted the response and retries transient failures up to three times.
- Generic command completion now also checks HTTP success instead of silently discarding failures.
- Regression coverage confirms a Question reply is visible in the admin dashboard payload.


## v0.3.6 reply capture fix

- Question replies are copied out of the WinForms textbox before the dialog closes/disposes, preventing empty replies from being sent to the server.


## v0.3.7: Machine-wide agent install

The new installer requires elevation and installs into `C:\Program Files\HomeWatch`.
It registers a machine-wide HKLM Run entry: each Windows desktop user starts the
agent when they sign in. The per-device enrolment record is now stored in
`C:\ProgramData\HomeWatch\config.json`, shared across profiles; per-user logs
remain under `%LOCALAPPDATA%\HomeWatch\agent.log`. Only the active local console
session sends activity and polls messages/commands, to avoid stale sessions
interfering after Fast User Switching.

**Migration:** run the new installer from the *same administrator account*
that ran the original per-user HomeWatch installation. It attempts to copy
`%LOCALAPPDATA%\HomeWatch\config.json` into ProgramData if no machine config
exists. Verify the ProgramData copy before deleting the old per-user config.
If UAC requires entering another administrator's credentials, you may need
to copy that original `config.json` manually into ProgramData.

**Updater limitation:** Existing unprivileged self-updating cannot overwrite
`Program Files`. Rather than quit and fail, the agent reports that a new
version requires the administrator-run installer. A trusted privileged
updater service is required to restore silent machine-wide updates.

**Security note:** ProgramData's shared configuration must be writable by
interactive users in this architecture, so local users can tamper with the
client credentials/config. This is not a tamper-proof enforcement system.
A privileged background service holding the credentials is the appropriate
long-term architecture.

## New in v0.4.0 — My Apps

The child-facing **HomeWatch Launcher** can be opened by double-clicking the HomeWatch tray icon or right-clicking and choosing **Open HomeWatch Launcher**. If you run the v0.4.4 installer, it also adds a **HomeWatch Launcher** Start Menu shortcut and uses the new HomeWatch logo for the app icon and web favicon. The portal lets children request executables, and the new **Applications** page in the web admin allows device, child or global approval. Fingerprints, signer information, revocation and launch history are included. See [RELEASE-v0.4.0.md](RELEASE-v0.4.0.md) for deployment and the important distinction between approved normal-user launch and Windows administrator elevation.


### Native x64 installation patch (v0.4.3a)

See `RELEASE-v0.4.3a.md` for the one-time migration procedure. Publish using numeric Git tag `v0.4.3.1` because the updater parses `System.Version`.


### v0.4.3b artwork hotfix

See `RELEASE-v0.4.3b.md` for server-only deployment instructions and the first-save artwork regression fix.


### v0.4.4a (numeric release v0.4.4.1): Windows build correction

Corrected the application icon call in the child-facing launcher to reference the `AgentContext` helper where it actually lives. See `RELEASE-v0.4.4a.md`.

## v0.4.5 — Household software library

The **Applications → Household software library** section lets an administrator publish a standalone `.exe` installer to all enrolled clients. Specify a Windows SMB/UNC share (for example `\\192.168.1.250\installers\game1\game1install.exe`) or direct HTTP(S) URL, plus an independently calculated SHA-256 checksum. The Windows Launcher verifies every downloaded/cached package against that hash before opening it as the current user. Installation attempts appear in the admin web UI. Globally managed friendly names and SteamGridDB posters use the same central metadata/caching workflow as existing application requests.

See [RELEASE-v0.4.5.md](RELEASE-v0.4.5.md) for installation requirements and important boundaries: single-file `.exe` installers only, SMB permissions under the child's account, no automatic detection of completed installations, no silent elevation, and no modification to the privileged HomeWatch updater's fixed release source.

## v0.5.0 — Interactive household installation from a remote NAS

HomeWatch now supports configuring the NAS share *inside the parent web UI*, without a Docker bind mount. It browses directories using SMB, hashes complete multi-file installation packages and exposes approved UNC sources to enrolled Windows clients. The clients stage and verify the full package before launching the normal interactive installer, then discover/request approval for the installed game's executable. See [RELEASE-v0.5.0.md](RELEASE-v0.5.0.md) for setup details and limits, including the separate SMB permissions required on each Windows PC.


### v0.5.1 faster package transfers and optional Defender configuration

See [RELEASE-v0.5.1.md](RELEASE-v0.5.1.md) for single-pass streaming SHA-256 transfer verification and the *one-time, locally administrator-approved* optional exclusion script. The HomeWatch agent and updater do not automatically change Defender settings.
