# HomeWatch v0.5.6 — Launch guard and running-game controls

The child-facing HomeWatch Launcher now guards against launching multiple copies of
the same game by repeatedly clicking Play, and offers a per-game Stop action.

## Behaviour

1. Clicking **Play** immediately reserves the selected game's executable path
   and displays **STARTING…** while HomeWatch checks the executable fingerprint
   and asks the server for launch permission. Additional clicks are ignored.
2. HomeWatch starts the game in the signed-in user's Windows session as before.
3. Every 1.5 seconds the launcher checks for a process in the **same Windows
   session** whose **full executable path** matches the approved game. It doesn't
   use process name alone to identify a running game.
4. Once that EXE is running, the action becomes **STOP GAME** (with a running
   indicator on the poster). This also works when the game was started outside
   HomeWatch, or the launcher is opened after the game started.
5. **STOP GAME** displays a warning about losing unsaved progress before
   force-closing matched processes. Only matching binaries in the current
   interactive session are targeted. The button is locked while closing.
6. When the game exits, the button returns to **PLAY** automatically. Game
   uninstallation is disabled while its EXE is running or starting.
7. If Windows never starts the expected EXE (e.g., a launcher wrapper exits
   without starting the approved game executable), the **STARTING…** reservation
   times out after 60 seconds and Play becomes available again.

## Limitations

- Detection intentionally tracks the **exact registered game executable**, not
  unrelated launchers or differently named child executables. Games that spawn
  another executable will need the parent-approved shared launch path pointed
  at the actual running game EXE to achieve reliable detection.
- Force close is equivalent to terminating a process: **unsaved game progress
  may be lost**. A confirmation dialog is required.
- The launcher never kills processes with only a matching name; if Windows
  prevents the launcher from inspecting a process's path, it won't identify or
  stop that process.
- The game-process state is local to the Windows client; **no server or
  PostgreSQL migration** is required.

## Release and deployment

1. Push to `main` and confirm both GitHub Actions jobs pass, particularly the
   Windows .NET build and existing Authenticode signature verification.
2. Publish the signed Windows release `v0.5.6`:

   ```powershell
   git tag v0.5.6
   git push origin v0.5.6
   ```

3. Use the HomeWatch dashboard **Update now** feature after ensuring the client's
   v0.5.4+ signed-update trust certificate is already configured.
4. Test with a real approved game: double-click Play rapidly, verify one process
   starts, observe the **STOP GAME** button, close normally, then test the
   confirmed force-close action. Verify another similarly named EXE in a
   different folder remains untouched.

## Signing and updater

The existing three-executable release package, PFX GitHub secrets, SignTool
signing, and independent osslsigncode checks remain unchanged. The HomeWatch
service still pins locally approved Authenticode signers on download and apply.

## Validation

Python/source-contract tests and JS/YAML checks are run in the development
container. Actual WinForms compilation and real process control require GitHub
Actions and a Windows desktop test.
