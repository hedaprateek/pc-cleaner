# PC Companion

A native Windows utility for live computer information and conservative temporary-file cleanup.

## Open the app

Double-click `launch.cmd`, or open `dist/win-x64/PC Companion.exe` directly. The published executable includes its .NET runtime, so the destination computer does not need a separate .NET installation. Use the x64 version on Intel/AMD Windows computers; build `win-arm64` for Windows ARM computers. This app does not support macOS or Linux.

Optional desktop and Start menu shortcuts:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-shortcuts.ps1
```

Keep the executable in its original location after creating shortcuts, or recreate the shortcuts after moving it.

## Features

- Live CPU utilization and RAM usage; available space on the Windows drive.
- **Performance insights** explain current readings with green normal, amber attention, red high, and blue observing/unavailable labels. CPU at 85%+ or RAM at 90%+ must stay elevated across samples for 20 seconds before an alert is shown. CPU/RAM at 95%+ are marked high after that period. Storage uses 10%/10 GiB attention and 5%/2 GiB high thresholds. These are guidance thresholds, not a diagnosis.
- **Apps** lists accessible processes, measured CPU usage between samples, and working-set memory. Sort by CPU or memory and open Task Manager to investigate. Protected or exited processes are counted as unavailable; new processes need a second sample for CPU. Working sets can include shared pages and should not be added together. A process using 20%+ CPU or a working set of 1 GiB+ is highlighted as an observation, not automatically a problem.
- Computer model, Windows edition/build, CPU cores and logical processors, installed RAM, GPU, storage devices and battery information where Windows exposes it.
- Capacity and available space for each accessible fixed drive.
- Copy computer details or export a text report.
- **Scan first** previews eligible file counts and file sizes without deleting anything.
- Detailed preview includes file paths, sizes, modification dates, eligibility/protection reasons, search, and status filters. Hover over a row to read its complete path and reason.
- Uncheck a file, keep selected files, or exclude a folder. Exclusions last for the current session and also apply to Quick Clean. Clear exclusions to reset them. Search/status filters affect visibility only; hidden selected files still belong to cleanup selection.
- **Quick Clean** scans and deletes eligible files immediately in the selected categories.
- **Clean scan results** deletes the previewed candidates after rechecking them.
- Cancellation and the latest 100 cleanup records.
- Cleanup results remain in the preview, with per-file deletion/skip reasons and not-processed status after cancellation. Export all preview/result entries as CSV, including entries hidden by a filter.
- Completion feedback reports deleted file sizes separately from measured available-space changes on accessible fixed drives. Other disk activity can affect the latter; neither figure is a benchmark or proof of a speed increase.
- Links to Windows storage and startup-app settings.

## Cleanup policy

The default category is the current user's known Local AppData `Temp` directory. Windows' known `Temp` directory is optional and subject to the current user's permissions. Paths come from Windows special-folder APIs rather than a fixed username or drive letter. Custom temporary directories outside these locations and browser caches are not included in this version.

Only files whose creation and last-write timestamps are both older than seven days are eligible. The app leaves recent files, files in use, read-only files, changed candidates, directory links, and inaccessible entries alone. It deletes individual files; it never recursively deletes directories. Deletion uses an exclusive Windows file handle and rechecks the resolved path and timestamps before deleting through that same handle. Scan traversal is capped at 100,000 entries; partial results are labeled.

Deleted files are not sent to the Recycle Bin and cannot be restored by this app. Cancellation stops between files and cannot undo completed deletions. Temporary-file age alone is not a guarantee that an application will never need a file again; close applications before cleaning and use the preview if unsure.

Documents, Downloads, cookies, browser passwords, Recycle Bin contents, registry keys, and Windows update stores are outside cleanup scope. Windows-managed storage cleanup is accessed through Windows Settings. The utility does not request administrator access or force-close applications.

History is stored locally in `%LOCALAPPDATA%\PcCompanion\history.json`. It records time, counts, and total logical file sizes, without file paths. These totals may differ from the actual disk space recovered. Detailed scan/result rows are held in memory until a new scan or app close. There is no app analytics or upload service. Exported computer reports include the computer name and current performance observations; preview CSV reports include local file paths. Review reports before sharing.

Storage cleanup can help a computer that is running out of space, but cannot guarantee improved CPU speed or solve every source of slow performance.

## Build

Requires the .NET 10 SDK on Windows. A local SDK in `.dotnet` is used if present; otherwise the script uses `dotnet` on PATH.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
# For Windows ARM:
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Runtime win-arm64
```

## Verify

The console checks use disposable fixtures inside `tests/fixtures`, never real user temporary files. They exercise age filtering, nested files, exclusive locks, changed files, read-only files, candidate containment, selected roots, file/folder exclusions, preview state, skip reasons, cancellation, junctions, sustained-alert thresholds, process sampling, and real read-only hardware queries.

```powershell
dotnet run --project .\tests\EngineTests.csproj -c Release -p:RestoreConfigFile="$PWD\NuGet.Config"
```

For an interface smoke test, launch the executable with `--ui-smoke-test` and an output PNG path. It opens the window, reads information, performs a read-only temporary-file scan, verifies checkbox exclusion/reinclusion using an in-memory UI fixture, captures cleanup/insights/apps screens, and closes without invoking cleanup. A `.result.txt` file reports the outcome.

Later additions: opt-in browser-cache cleanup, schedules, system tray access, and theme selection.
