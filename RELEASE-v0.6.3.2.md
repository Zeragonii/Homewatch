# HomeWatch v0.6.3.2 — Wellbeing Reminder Foreground Hotfix

This release brings hydration, toilet and custom wellbeing reminders to the foreground using the same Win32 topmost / restore / foreground activation path as HomeWatch’s parent Alert and Question windows. The popup retries foreground activation six times while appearing, starts focused on **Done!**, and cannot be dismissed through the title bar, Alt+F4 or Escape. The only allowed actions are **Done!** and, when the parent enables it, **In N minutes**.

The reminder scheduler continues to run from the tray agent, independent of whether the child has the HomeWatch Launcher open. Existing rules, time counters, snoozes, event history, active-time tracking and screen-time policy are unchanged. If a popup unexpectedly fails to initialise, it is **not** recorded as acknowledged; HomeWatch retries the reminder after 30 seconds instead.

### Deployment

Publish the normal signed tag `v0.6.3.2`. A machine already on `v0.6.3.1` can receive this through the existing remote updater—no manual installer or server/DB changes are required. The unchanged installer broker remains compatible; GitHub Actions may still publish a separately versioned broker binary as part of its existing release workflow.

### Test on the child's Windows account

1. In the parent Web UI, create or temporarily edit a wellbeing reminder with a short interval. To speed up testing without altering active-time semantics, use **scheduled** mode at the next available local time, or the minimum allowed timer interval.
2. Put a game into a normal window or borderless fullscreen and leave it focused. Wait until HomeWatch raises the reminder, which should come to the front and remain topmost.
3. Try Alt+F4 and Escape; neither should count as acknowledgment. Only Done! or the allowed snooze should complete it.
4. Confirm the parent event history records the actual action and a snoozed reminder reappears when its delay expires.
5. Test an exclusive-fullscreen game separately: Windows foreground restrictions and some exclusive fullscreen renderers can prevent ordinary topmost windows from appearing above games. HomeWatch does not forcibly kill or minimise a game.

### Scope and caveats

This is a *Windows Launcher/agent UI fix*. It uses the existing pinned foreground API shared with Alerts; it does **not** bypass Windows focus protection, alter administrator permissions or launch an elevated UI. Actual foreground behaviour must be verified on Windows after GitHub Actions compiles the client.
