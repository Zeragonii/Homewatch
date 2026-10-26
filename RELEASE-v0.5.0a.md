# HomeWatch v0.5.0a — PostgreSQL startup hotfix

## What broke

The new v0.5.0 `household_catalogue.enabled` column was declared `BOOLEAN DEFAULT 1`. SQLite accepted this in development, but PostgreSQL rejected the `CREATE TABLE` statement with `DatatypeMismatch`. This prevented server startup.

## Correction

The column now uses `server_default=text("true")`. A new PostgreSQL-dialect schema regression test checks that no boolean columns use `0` or `1` SQL defaults. No existing data migration or database reset is necessary for this fix.

## Deploy server-only

1. Back up the existing HomeWatch PostgreSQL volume as you normally would; **do not delete or recreate it**.
2. Replace repository contents with this full repository; commit and push to **main**.
3. Wait for the new `ghcr.io/zeragonii/homewatch-server:main` image to finish building.
4. In Portainer, pull that new server image and redeploy HomeWatch using the **same PostgreSQL volume** and environment variables.
5. Confirm the server starts and NAS installer configuration becomes available.

This is **server-only**. Windows agent version remains **0.5.0**; there is no need for a new Windows release tag, agent reinstall, or client update. If a `v0.5.0` Git tag/release already exists, leave it unchanged.

## Tests

Run `PYTHONPATH=. pytest -q tests`. The PostgreSQL SQL DDL is compiled and validated without requiring a live PostgreSQL instance; a full live-database smoke test still requires a running PostgreSQL service.
