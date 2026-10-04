# Squeue manual tests

These are the checks the automated tests can't do: real drives, real clicks, real interruptions. Mark each one ✅ or ❌, and for a ❌ write what you saw (a screenshot helps).

## Before you start

Create the test card (about 600 photos and two large videos, about 5 GB):

```powershell
powershell -ExecutionPolicy Bypass -File tests\manual\make-test-data.ps1
```

This makes `Desktop\SqueueTest\Card\DCIM` (the "card") and `Desktop\SqueueTest\Backup` (the destination). Use `-Root D:\SqueueTest` to put it elsewhere, or `-LargeGB 1` for smaller videos.

After any copy, check the result independently of Squeue (it uses Windows' own SHA-256):

```powershell
powershell -ExecutionPolicy Bypass -File tests\manual\compare.ps1 -Source "$HOME\Desktop\SqueueTest\Card\DCIM" -Destination "$HOME\Desktop\SqueueTest\Backup\DCIM"
```

It prints `PASS`, or lists missing files, different files and leftover `~tq….tmp` temp files.

For a fresh start between tests, delete `Desktop\SqueueTest\Backup\DCIM`.

You'll also need, for some tests: a USB stick, and a second drive (another USB stick or internal disk).

## A. Install

| # | Do this | Expect | Result |
|---|---|---|---|
| A1 | Open `dist\SqueueSetup.exe` | Possibly "Windows protected your PC": More info → Run anyway. No administrator prompt. Wizard with the Squeue icon. | |
| A2 | Finish the wizard with "Launch Squeue" ticked | Squeue opens on the Queue tab with the drop area and "How it works" | |
| A3 | Open the Start menu, type Squeue | Squeue is listed with its icon and opens | |
| A4 | Open Squeue a second time while it's open | Still only one window | |

## B. Copying

| # | Do this | Expect | Result |
|---|---|---|---|
| B1 | **Copy a folder…** → pick `Card\DCIM` → choose `Backup` → **Start copying** | The plan shows 606 files. The card shows "Copying · speed", then "Checking", then finishes. History shows it under Today as "All checked". | |
| B2 | Run `compare.ps1` | `PASS` | |
| B3 | Delete `Backup\DCIM`. Drag the `DCIM` folder from Explorer onto the window | Same as B1, and `compare.ps1` passes | |
| B4 | **Copy files…** → pick 3 photos → `Backup` | The 3 files appear directly in `Backup` | |
| B5 | Check `Backup\DCIM\Odd names` | `empty.txt`, `Café Été — 写真 ✓.jpg`, the name with double spaces and the very deep `deep-file.jpg` are all there (compare covers them) | |

## C. Pause and cancel

| # | Do this | Expect | Result |
|---|---|---|---|
| C1 | Start B1 again (fresh Backup). While a big video is copying, **Pause**. Wait 10 s. **Resume** | Progress stops, then carries on from where it was, not from 0. `compare.ps1` passes. | |
| C2 | Start again (fresh Backup). Halfway, **Cancel** | Card says Cancelled. History shows Cancelled. `Backup` has no `~tq….tmp` files. | |

## D. Files that already exist

| # | Do this | Expect | Result |
|---|---|---|---|
| D1 | After a complete B1, copy `DCIM` to `Backup` again (Settings → existing files: **Skip it**) | Finishes quickly. Nothing in `Backup` changes (check a file's Modified time). | |
| D2 | Open `Backup\DCIM\Odd names\empty.txt` in Notepad, type something, save. Copy `DCIM` again with **Skip it** | `empty.txt` keeps your text. The job reports it as a file that wasn't replaced. | |
| D3 | Same, but Settings → **Replace it** (or choose it for this job only in the plan) | `empty.txt` is empty again. `compare.ps1` passes. | |

## E. A file that fails, then retry

| # | Do this | Expect | Result |
|---|---|---|---|
| E1 | In a separate PowerShell window: `powershell -ExecutionPolicy Bypass -File tests\manual\lock-file.ps1 "$HOME\Desktop\SqueueTest\Card\DCIM\100CANON\IMG_0007.JPG"` and leave it waiting | "Locked: …" | |
| E2 | Copy `DCIM` to a fresh `Backup` | Ends as "Done · 1 failed". The History tab title says "1 needs attention". | |
| E3 | In History, **Show failed files** | `IMG_0007.JPG`: "The source is in use by another program." | |
| E4 | Press Enter in the lock window, then **Retry failed** | Only that one file is copied. The job becomes "All checked". `compare.ps1` passes. | |

## F. Interruptions (the important ones)

| # | Do this | Expect | Result |
|---|---|---|---|
| F1 | Copy `DCIM` to a **USB stick**. During a big video, pull the stick out | The job pauses with "A drive stopped responding. Reconnect it and resume." The app doesn't crash. | |
| F2 | Plug the stick back in, **Resume** | Finishes. `compare.ps1` against the stick passes, with no temp files left. | |
| F3 | Copy to a fresh `Backup`. Halfway, end Squeue in Task Manager (End task) | — | |
| F4 | Open Squeue again | The job is still there and finishes (by itself or with Resume). `compare.ps1` passes with no temp files. | |
| F5 | Copy `DCIM` to a USB stick with **less free space** than 5 GB | Squeue says there isn't enough space instead of filling the stick | |

## G. Several drives

| # | Do this | Expect | Result |
|---|---|---|---|
| G1 | Start two copies to **two different drives** | Both copy at the same time. The drive row shows both drives as busy (filled dots). | |
| G2 | Start two copies to the **same drive** | The second shows "Waiting · …" and starts when the first finishes | |

## H. History and settings

| # | Do this | Expect | Result |
|---|---|---|---|
| H1 | Close Squeue and open it again | **All earlier jobs are still in History.** (An earlier history once went missing; this is the check for it.) | |
| H2 | In History, **Open folder** on a job | Explorer opens the destination | |
| H3 | Settings: untick **Check every file after copying**, copy something | There's no "Checking" step. History says "Copied, not checked". | |
| H4 | Settings → When everything is done: **Play a sound**, then **Flash the taskbar button** (minimise Squeue while it copies) | You hear the sound, then the taskbar button flashes when the queue finishes | |
| H5 | Change settings, close and reopen Squeue | The settings are kept | |
| H6 | Turn on Windows dark mode (Settings → Personalisation → Colours) | Squeue follows it and stays readable | |

## I. Uninstall

| # | Do this | Expect | Result |
|---|---|---|---|
| I1 | Windows Settings → Apps → Installed apps → Squeue → Uninstall | Removed. No Start menu entry left. | |
| I2 | Check `%LOCALAPPDATA%\Squeue` | Still there (history and settings are kept for a reinstall). Delete it by hand to remove everything. | |

## Already checked by Claude (2026-10-04)

- Silent install into a test folder: exit code 0, `Squeue.exe` identical to the build, Start menu shortcut created, uninstall entry "Squeue 0.1.0 · Salim May" registered, no desktop shortcut unless ticked.
- The installed app starts and shows its window, and closes cleanly.
- Silent uninstall: program, shortcut and uninstall entry removed. `%LOCALAPPDATA%\Squeue` kept.
- `make-test-data.ps1` creates 606 files (including non-English, empty and over-260-character paths), and `compare.ps1` passes on an exact copy and catches a missing file.
