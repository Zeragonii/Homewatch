# HomeWatch v0.4.4 — branding and launcher shortcut polish

## What's new

- Uses the new uploaded HomeWatch logo as the **web logo**, **favicon**, **Windows app icon**, and **installer icon**.
- Renames the child-facing shortcut from **HomeWatch My Apps** to **HomeWatch Launcher**.
- Renames the tray action to **Open HomeWatch Launcher**.
- Updates the per-user desktop shortcut creation logic so existing installs can receive the new shortcut name after an agent update.

## Deployment

1. Push to `main` and let GitHub Actions build.
2. Tag and publish:

```powershell
git tag v0.4.4
git push origin v0.4.4
```

3. Update the server if needed (branding/favicon changes live there).
4. Use **Update now** to push the Windows agent and installer update.

## Notes

- The server serves `server/app/static/homewatch-logo.png` as the web logo and favicon source.
- The Windows executable embeds `agent/HomeWatchAgent/HomeWatch.ico` as its application icon.
- The installer uses the same icon via `SetupIconFile=HomeWatch.ico`.
