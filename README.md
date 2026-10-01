# Squeue

Squeue is a copy manager for Windows. It copies files safely, checks them afterwards, and picks up where it left off if something goes wrong.

## Build

```powershell
./build.ps1
```

This runs the tests and writes a single `Squeue.exe` to `dist\Squeue`. Use `./build.ps1 -SkipTests` to skip the tests.

## Run from source

```powershell
dotnet run --project src/Squeue.App
```

## Where data lives

Squeue keeps its queue, history and settings in `%LOCALAPPDATA%\Squeue`.

## Status

Built:

- Crash-safe copy with checksum checks
- Pause, resume and cancel
- Recovery after a crash or an unplugged drive
- One job per physical drive, with jobs on different drives running in parallel
- Free-space check before starting
- History with failed files and retry
- Settings

Next:

- Per-file conflict choices: keep both, replace if newer, ask
- Moves
- Read-only, metadata and exFAT handling
- Queue reordering and "safe to remove" for cards
- Copy again from history
- Windows notifications
- Explorer right-click menu and installer
