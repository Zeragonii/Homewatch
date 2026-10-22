# HomeWatch v0.3.9 build repair

Fixes CS8803 in `agent/HomeWatchService/Program.cs`: top-level statements now precede all type declarations.

The Windows GitHub Actions build now immediately fails when any `dotnet publish` returns a nonzero exit status. This avoids confusing follow-on `Copy-Item` errors.

**Validation caveat:** the source was structurally checked in this environment; the Windows service binary must still be compiled by GitHub Actions on `windows-latest`.

Publish as `v0.3.9` after pushing the changed files to main. Do not reuse an already published tag.

**Security note:** release-side SHA-256 files do not authenticate a publisher. Treat this updater as experimental until code signing/verification and rollback are implemented.
