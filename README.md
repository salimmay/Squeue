# Squeue

Squeue is a copy manager for Windows. It copies files safely, checks them afterwards, and picks up where it left off if something goes wrong.

## Build

```powershell
./build.ps1
```

This runs the tests and writes a single `Squeue.exe` to `dist\Squeue`. Use `./build.ps1 -SkipTests` to skip the tests.

To also build the installer (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
./build.ps1 -Installer
```

This writes `dist\SqueueSetup.exe`. It installs for the current user without an administrator prompt.

## Website

The landing page is in `site/`. The Pages workflow publishes it on every push to `main`. Its download button points at `SqueueSetup.exe` in the latest GitHub release.

## Run from source

```powershell
dotnet run --project src/Squeue.App
```

## Explorer and the command line

The installer adds **Copy with Squeue...** (files, folders, drives) and **Paste with Squeue** (folders, drives, the empty space in a folder) to Explorer's right-click menu. On Windows 11 they are under **Show more options**.

```powershell
Squeue.exe G:\DCIM                       # copy, ask where to
Squeue.exe --to E:\Backup G:\DCIM         # copy into E:\Backup
Squeue.exe --paste E:\Backup              # copy the files on the clipboard (Ctrl+C in Explorer) into E:\Backup
```

If Squeue is already open, a new launch hands its command line to the open window. Items selected together in Explorer become one job.

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
