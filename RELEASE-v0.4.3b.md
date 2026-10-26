# HomeWatch v0.4.3b — artwork first-save hotfix

This is a **server-only** fix based on the complete v0.4.3a repository. No Windows agent, updater, installer, device identity, or update-repository changes are required.

## Fixed

- First poster save when there is no existing `ApplicationPresentation` row: `artwork_revision` may be `None` until SQLAlchemy flushes/commits the new row. The API now treats this as zero and saves revision 1 instead of returning HTTP 500.
- Bootstrap normalization: at startup, any legacy `application_presentations.artwork_revision IS NULL` values are set to zero without changing artwork, names or approvals.
- New presentations specify a database-side revision default of zero for newly created schemas.
- First poster save responds with the executable's friendly fallback name rather than an empty label.
- Added an API regression test for artwork-first assignment, a second artwork update, and local agent image retrieval.

## Deploy to Portainer

1. Copy the updated repository contents into your GitHub repo and push a **new commit to `main`**.
2. Confirm the `server-tests` and Docker image build pass in GitHub Actions.
3. Pull/redeploy `ghcr.io/zeragonii/homewatch-server:main` in Portainer (enable re-pull/latest image if offered). Keep the existing PostgreSQL and artwork volumes intact.
4. Reload Applications in the web admin UI; select the poster for Little Kitty, Big City again.

**No Windows installer or Git tag is needed** for this server-only fix. The Windows binaries still use the existing 0.4.3.1 base version, so this will not inadvertently trigger an agent update. Do not reset the PostgreSQL volume.

If the server was deployed from a different branch than v0.4.3a, reconcile repository changes before replacing them.

## Verification

```
PYTHONPATH=. pytest -q tests
python -m compileall -q server/app
node --check server/app/static/app.js
```

The SteamGridDB API response and image-download code are unchanged. No external API calls are made by tests.
