# HomeWatch v0.1

A self-hosted, multi-device parental monitoring MVP.

## Included in v0.1

- Multi-child / multi-device data model
- Server-controlled device identity
- Zero-UUID-touch enrolment: agent submits a pending request, admin approves it
- Re-enrolment onto an existing logical device without splitting history
- Agent heartbeat / online state / current foreground application
- Per-day application runtime aggregation
- Parent-to-device messages
- On-demand screenshots
- Agent release promotion API and self-update plumbing
- Docker Compose deployment with PostgreSQL
- Windows tray agent + updater (.NET 8 / win-x64)
- GitHub Actions workflow that builds server image and Windows release ZIP

## Architecture

```text
Windows tray agent  --HTTPS-->  HomeWatch API  <--> PostgreSQL
      |                               |
 foreground app                  Parent web UI
 screenshots                     enrol / stats
 messages                        commands
 updater                         releases
```

The agent installation has its own `installation_id`, but the **logical device UUID is created by the server only when an admin approves enrolment**. A reinstall can therefore be bound back onto an existing device and keep the same history.

## Quick start (server)

1. Copy `.env.example` to `.env` and change the secrets/password.
2. Run:

```bash
docker compose up -d --build
```

3. Open `http://SERVER-IP:8090`.
4. Sign in with the `ADMIN_USERNAME` / `ADMIN_PASSWORD` values from `.env`.
5. Create a child profile.

For real use outside your LAN, put the service behind HTTPS (for example your existing reverse proxy). **Do not expose the agent API over plain HTTP on the public Internet.**

## Agent first run

The Windows agent is designed as a per-user tray application. On first launch it asks only for the HomeWatch server URL. It then appears under **Pending devices** in the admin UI.

Approve it and either:

- create a new logical device; or
- replace the installation attached to an existing device.

The permanent device UUID never needs to be typed by a human.

Agent state is stored in:

```text
%LOCALAPPDATA%\HomeWatch\config.json
```

## Build the Windows agent locally

Requires the .NET 8 SDK on Windows:

```powershell
dotnet publish agent/HomeWatchAgent/HomeWatchAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish agent/HomeWatchUpdater/HomeWatchUpdater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

GitHub Actions also does this automatically on tags matching `v*`.

## Self-updates

The server is the release authority. Promote an agent release using the API (UI control is the next polish pass):

```http
POST /api/releases
{
  "version": "0.1.1",
  "url": "https://github.com/OWNER/REPO/releases/download/v0.1.1/HomeWatch-Agent-win-x64.zip",
  "sha256": "...",
  "channel": "stable",
  "promoted": true
}
```

Agents poll `/agent/update` and download only a promoted release newer than themselves. The SHA-256 is verified before the updater runs.

## Safety / privacy defaults

HomeWatch is intentionally designed as visible parental-management software rather than covert surveillance:

- tray icon is visible;
- enrolment requires admin approval;
- screenshots are on-demand, not continuously archived;
- the agent does not include keylogging, microphone capture, credential capture, or stealth/persistence tricks;
- credentials are revocable and per-device.

## Current limitations

This is a bootstrap MVP, not yet production-hardened:

- no WebRTC/live video yet;
- no MSI/setup wizard yet (GitHub produces portable release binaries);
- no role-based parent accounts yet;
- update release promotion currently uses the API rather than a polished UI;
- no code-signing step yet; the workflow has a clearly marked place to add it;
- screenshot storage is local filesystem storage on the server;
- automated tests cover core server logic only.

## Suggested next milestones

- v0.1.1: release-management UI, device detail page, charts, installer
- v0.2: schedules/time limits, lock/logout/shutdown commands
- v0.3: live screen viewing (WebRTC), PWA manifest/service worker, push notifications
