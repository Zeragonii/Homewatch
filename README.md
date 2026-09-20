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
