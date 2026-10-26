# HomeWatch v0.4.2 — My Apps poster-grid launcher

## What's new

- Child-facing My Apps interface uses a portrait tile grid with a click-to-expand details pane, approval status and Play / Open button.
- Requesting a new app now opens a dedicated dialog using **+ Request a new app**. No permanent request controls on the main screen.
- If a game doesn't have central artwork yet, its Windows executable icon appears on a gradient fallback tile.
- Admin **Applications → Edit tile** can set a friendly name and search/select SteamGridDB 600×900 poster art.
- Central application presentation is keyed by executable SHA-256; one edit updates every enrolled client with that same binary fingerprint.
- Selected artwork is downloaded **once** by the HomeWatch server, converted to a constrained JPEG, and stored on persistent local server disk. Windows clients cache the JPEG by SHA-256 and revision under `%LOCALAPPDATA%\HomeWatch\artwork`.
- New source code adds no executable to the machine-wide updater's three-EXE allowlist, making this a useful test for the v0.4.1 updater repair.
- Approval, request, signature and normal-user launching behaviour are unchanged. **This is not a UAC elevation feature.**

## Deploy

1. Commit and push the repo to `main`. Verify the server smoke tests and Windows agent job are successful.
2. In Portainer, pull `ghcr.io/zeragonii/homewatch-server:main` and redeploy the stack.
3. Obtain an API key from the SteamGridDB [preferences page](https://www.steamgriddb.com/profile/preferences). Add the *server-side* environment variable `STEAMGRIDDB_API_KEY` to the Portainer app service. Never embed the key in browser/Windows clients.
4. Restart the HomeWatch server so it picks up that key. In the **Applications** tab, click **Edit tile** to rename an app and select a SteamGridDB game + poster.
5. After the Windows job succeeds, publish a tag `v0.4.2` (never move/reuse an existing tag). The GitHub Release must contain `HomeWatch-Agent-win-x64.zip` and its `.sha256` file.
6. From HomeWatch admin, click **Update now** on a v0.4.1 client and verify update status changes through checking/downloading/installing to current v0.4.2. Confirm the new tile layout after the agent restarts.

## Artwork storage and permissions

- When no `ARTWORK_DIR` is provided, cached posters default to `<SCREENSHOT_DIR>/artwork`. This is intentionally under the existing screenshot volume so existing Portainer deployments retain their artwork without additional volume changes.
- If using the included repo `docker-compose.yml`, `.env.example` sets `ARTWORK_DIR=/data/artwork`, and a distinct named volume `homewatch_artwork` is mounted to persist it. Do not configure a separate path in Portainer without mounting a persistent volume to it.
- Only an authenticated parent can search, select and rename. Only the already-enrolled device that submitted the application can fetch its poster.
- External artwork downloads accept only HTTPS SteamGridDB-controlled asset hosts, reject redirects, limit size and dimensions, and transcode to JPEG. The chosen ID is resolved on the server, never from an arbitrary child-provided URL.
- SteamGridDB art belongs to its respective creators. Check the provider's current usage terms; selecting a poster is not a transfer of copyright.
- The client requests a new cached copy only when the art revision increments. Friendly-name changes don't invalidate artwork.

## Notes and limitations

- If SteamGridDB isn't configured, admin can still change friendly names; art search shows a clear `STEAMGRIDDB_API_KEY` configuration error.
- No externally fetched artwork is required for normal HomeWatch operation.
- C# source is covered by the GitHub Actions Windows publish step. .NET SDK is unavailable in the current build environment, so the Windows binary cannot be locally compiled or install-tested here. Publish v0.4.2 only after the GitHub job is green.
- Machine-wide silent updates depend on the installed updater service (v0.4.1 or newer). This release does not fix an older updater that fails before it can apply updates.
