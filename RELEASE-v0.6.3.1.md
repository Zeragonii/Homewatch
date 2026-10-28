# HomeWatch v0.6.3.1 — Broker Auto-Update Build Hotfix

This is a corrected v0.6.3 bridge release. The v0.6.3 Windows GitHub Actions job stopped before compilation because a workflow preflight rejected a `Process.Start(...) ?? throw` expression in `HomeWatchService/Program.cs`. The source now uses an explicit null check while preserving the identical behaviour. The workflow's existing preflight is left in place, and the repository includes a regression test running the same regular expression.

## Why publish as v0.6.3.1?

GitHub release tags refer to commits. A failed `v0.6.3` workflow cannot be repaired merely by pushing a corrected `main` branch and rerunning the old tag. Publish **`v0.6.3.1`** from the corrected commit instead of deleting or rewriting an existing tag. The fallback workflow version, both `.csproj` file versions and Windows installer version were updated to `0.6.3.1`.

## Remote upgrade from v0.6.2

The v0.6.2 updater accepts only the original three signed executable files. The v0.6.3.1 workflow deliberately publishes the same three-file ZIP plus a separately signed broker asset (`HomeWatchInstallerBroker-win-x64.exe` and checksum). An existing v0.6.2 service first installs the original three binaries. Once its newer update service restarts, a subsequent update check recognises that the separately installed broker is older than the release, verifies its signer against the locally approved pin, and upgrades the registered broker service, with rollback on failure. **No manual installer should be needed on machines where the installer broker was already installed and locally enrolled.**

See [RELEASE-v0.6.3.md](RELEASE-v0.6.3.md) for complete broker-update design, safeguards and troubleshooting.

## Publish

```powershell
git add .
git commit -m "Fix service Process.Start guard for broker auto-updates"
git push origin main
git tag v0.6.3.1
git push origin v0.6.3.1
```

Only tag once the corrected commit passes the tests and Windows build on `main`. If another `v0.6.3.1` tag already exists, choose the next unused version; do not force-push a release tag.

## Validation

Run `PYTHONPATH=. pytest -q tests` and `node --check server/app/static/app.js`. The GitHub Actions Windows runner must perform the .NET compile, signing and update packaging; live on-device broker replacement remains untested in this environment.
