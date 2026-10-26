# HomeWatch v0.4.4a — Windows launcher compile fix

This is a focused follow-up to v0.4.4, retaining the supplied icon/favicon and shortcut rename.

## Fix

The Windows UI used `Program.LoadBrandIcon()`, but the helper was declared on `AgentContext`, so `dotnet publish` failed with CS0117. The call now correctly uses `AgentContext.LoadBrandIcon()`.

## Publishing

The human patch label is **v0.4.4a**, but the numeric .NET / auto-update version is **0.4.4.1**.

1. Commit and push these complete repository files to `main`.
2. Confirm the Windows agent builds successfully on GitHub Actions.
3. Publish the release:

```powershell
git tag v0.4.4.1
git push origin v0.4.4.1
```

You do not need to remove old device registrations or reset PostgreSQL.

## Validation

Python tests and workflow YAML were checked. This container lacks the .NET SDK; **Windows compilation must be verified by GitHub Actions**.
