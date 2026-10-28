# HomeWatch v0.6.4.1 — Large portable-game scan fix

Based directly on v0.6.4 (itself based on the recovered v0.6.3.2 release).

## What was wrong

The NAS folder browser had a hard-coded **500 entries per directory** limit, and the package scanner reused that function. Publishing a prepared game containing a directory with 501 or more files failed before hashing. Portable packages also inherited a 4,000-file count limit from installer packages.

## Fix

- The server can now enumerate up to **120,000 total directory entries**, with a **100,000-file portable package limit** and the previous **300 GiB** size limit; nested directories are still restricted to 12 levels. Traditional installer packages retain their **4,000-file** limit to match the privileged broker.
- The admin NAS browser displays **250 entries per page** with Previous/Next controls; the package scanner always enumerates **all** files, regardless of which page is visible.
- SHA-256 hashing uses four parallel, bounded SMB readers (at most eight queued tasks), hashing each file once. Manifest ordering, total-byte accounting and byte-level integrity checking are preserved.
- The Windows **portable deployment** validator now accepts up to 100,000 files to match the server, without changing the privileged installer-broker limit.
- No database migration, new service or changed signing policy is needed.

The hash time depends on the NAS, file sizes and SMB latency. This avoids the arbitrary entry cap and can improve throughput for folders with many small files, but does **not** make hashing instantaneous or skip integrity verification.

## Release procedure

```powershell
git add .
git commit -m "v0.6.4.1"
git push
git tag v0.6.4.1
git push origin v0.6.4.1
```

Redeploy the HomeWatch server before rescanning the game. The Windows agent's new file-count guard is included in the signed release; deploy it before installing packages with more than 4,000 files.

## Validation

API tests run with `PYTHONPATH=. pytest -q tests`; frontend syntax with `node --check server/app/static/app.js`. The Windows build and live NAS scan require GitHub Actions and a Windows client.
