# HomeWatch v0.5.5 — Game Uninstallation

The child-facing HomeWatch Launcher now offers **Uninstall game…** on a locally installed household-catalogue game.

## Behaviour

1. The child confirms removal. This is a **local action on that one PC**, not a household-wide deletion or remotely triggered privileged command.
2. HomeWatch checks Windows installed-programs entries (HKLM/HKCU, 64-bit/32-bit) for an exact installation-directory or executable-icon match. If exactly one match exists, it starts that registered Windows uninstaller interactively. Standard Windows permission and UAC rules apply. For MSI product entries, HomeWatch uses `msiexec /x {PRODUCT-GUID}`.
3. If no uninstaller is found, HomeWatch offers **permanent removal of the complete game directory**, but only when it is an immediate child of a parent-configured games-library folder (`C:\Games`, `D:\Games`, or your configured roots). The full path is displayed and requires an extra confirmation. No network shares, drive roots, system/Program Files directories, installer caches, or folders containing reparse points/junctions are accepted. If no safe directory is identified, removal stops and tells the user to ask an administrator.
4. The installer status is updated only for the requesting device. The launcher resets the tile to **Install** after successful removal; errors are shown rather than silently marking the game removed.
5. The Web UI's existing catalogue activity/history includes `removed` and `remove_failed` events. Previous application approvals are kept in the audit history.

## Database changes

The server automatically adds `household_installations.install_root` (TEXT, default empty) on startup. **Do not delete or reset your PostgreSQL volume.** New installations already report their detected root; older installations fall back to registry identification or a dedicated folder beneath a configured game-library root.

## Deployment

Push this full repository to `main` and redeploy `ghcr.io/zeragonii/homewatch-server:main` in Portainer. After the Windows build/signing job succeeds, publish tag `v0.5.5` and use the existing signed updater. **Ensure v0.5.4 trust/certificate setup is complete on the clients first**, because subsequent updates require the pinned signer.

The three signed Windows executables and the updater's archive format have not changed. No additional service privileges or generic elevated deletion commands have been added.

## Limitations

- Registry-based uninstallers sometimes spawn a secondary UI and exit early. HomeWatch does not report success while the original game executable remains. Follow up with Windows Installed Apps if needed.
- A standard Windows user cannot remove administrator-protected game files without normal Windows elevation.
- Game saves/mods kept inside the deleted game folder will be lost. Saves stored separately are not automatically removed.
- Shared/ambiguous or unrecognised installation locations are intentionally not deleted recursively; use the Windows uninstaller or remove manually as administrator.
- Real Windows and .NET builds still need validation through GitHub Actions and an actual Windows client.
