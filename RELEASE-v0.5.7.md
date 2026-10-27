# HomeWatch v0.5.7 — Wellbeing Reminders

## Purpose

A friendly, non-punitive hydration/toilet-break reminder system designed to work from the logged-in user's existing HomeWatch tray agent, **without opening HomeWatch Launcher**. This does not change, block or pause games and is independent of screen-time policy enforcement.

## Parent setup

1. Deploy the updated server image and keep the existing PostgreSQL volume.
2. In HomeWatch's admin web UI, open **Wellbeing**.
3. Select a child, choose *Hydration*, *Toilet* or *Custom*, configure the schedule and message, then **Save reminder**.
4. Add a second reminder for a different kind; each rule has an independent timer. For example, hydration after 60 minutes of active use and a toilet reminder after 90 minutes.
5. Choose **Accumulated active time**, **Clock time while logged in**, or **Specific daily times** (`HH:MM`, comma-separated). For scheduled times, the **Windows PC's local time zone** is used.
6. Enable an optional snooze (default 5 minutes), adjust the inactivity cutoff (default 180 seconds), or disable a reminder without deleting history.
7. View **Recent reminder activity** for shown, snoozed and acknowledged events. An acknowledgement is a UI interaction, **not proof of completing the break**.

## Windows behaviour

- The existing per-user HomeWatch tray agent polls enabled reminders assigned to the child's profile. The separate game launcher window can be closed.
- **Accumulated active time** counts periods when the foreground Windows session is unlocked and recent keyboard/mouse activity is detected. It is **not tied to an individual game**. Time spent inactive (after the configured idle threshold), locked, or in another Windows session stops the counter.
- **Clock time** counts elapsed time while the user's console session is unlocked, even when ordinary keyboard/mouse input stops. Sleeping/offline sessions do not catch up missed hours on wake.
- **Scheduled times** only display a slot while the computer is in use, and no more than 30 minutes after its configured time. Slots missed while the PC is unused for longer than that are skipped, not piled up.
- When a reminder is due during idle time, display is delayed until there is user activity. Topmost, friendly reminder windows offer *Done* and optionally *Remind me in N minutes*. Multiple due rules are staggered rather than stacked.
- Rule progress and event logs are persisted in `%LOCALAPPDATA%\HomeWatch\wellbeing-state.json`. New events are queued locally if the server is offline and retried automatically. No local administrator permission is needed.
- The Windows user can still dismiss the reminder through its action buttons. This is **not a forced break**.

**Known limitation:** keyboard/mouse idle detection may not recognise games played solely using a controller. True exclusive-fullscreen games may obscure an ordinary topmost window. Test with the child's preferred games; controller activity and more robust full-screen reminders can be improved separately.

## Deploy

1. Push the full v0.5.7 repository to `main`.
2. Wait for API tests, frontend lint, Windows build, signing and verification to pass in GitHub Actions.
3. Redeploy `ghcr.io/zeragonii/homewatch-server:main` in Portainer with existing environment variables and volumes. New `wellbeing_rules` and `wellbeing_events` tables are created on startup; no PostgreSQL reset or manual migration is required.
4. Publish a signed release and update the Windows clients using the already-tested updater:

```powershell
git tag v0.5.7
git push origin v0.5.7
```

5. Test first using a single 5-minute active-time reminder on a Windows client, checking popup, snooze, acknowledgement, restart persistence and web history. Increase to the desired 60/90-minute intervals once verified.

## Verification scope

- Python regression suite and PostgreSQL DDL rendering checked; new rules/events route tests added, with authentication and cross-child isolation.
- JavaScript syntax and GitHub workflow YAML checked.
- **Windows client not compiled or tested in this environment**: GitHub Actions and a real Windows client still need to verify the popup, idle detection, controller limitation and persistence.

The existing signed three-EXE HomeWatch update pipeline remains unchanged except for the 0.5.7 version number.
