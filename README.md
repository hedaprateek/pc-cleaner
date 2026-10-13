# PC Companion

A native Windows utility for understanding slowdowns, exploring storage, reviewing startup apps, and controlling temporary-file cleanup.

## Download

Portable Windows x64 builds are published under [GitHub Releases](https://github.com/hedaprateek/pc-cleaner/releases).
Extract `PC-Cleaner-win-x64.zip` and open `PC Companion.exe`. This is Windows software,
not an Android APK. The executable is unsigned, so Windows may request confirmation.

The download includes **START HERE.txt** with extraction and task instructions. On first launch, **Getting started** shows steps for cleanup, live apps, storage analysis and startup review, with buttons that open each task. **Help / Getting started** reopens the guide at any time. Choose **Show this guide every time I open the app** before opening a task if you want it on every launch. Otherwise it is remembered as completed for your Windows account. Each main task also shows its steps within the screen.

The **Build Windows download** workflow can create a release from the Actions tab.
It runs the fixture tests, builds the portable executable, performs the read-only
interface smoke test, and attaches the ZIP before making the release public.

## Open the app

Double-click `launch.cmd`, or open `dist/win-x64/PC Companion.exe` directly. The published executable includes its .NET runtime, so the destination computer does not need a separate .NET installation. Use the x64 version on Intel/AMD Windows computers; build `win-arm64` for Windows ARM computers. This app does not support macOS or Linux.

Optional desktop and Start menu shortcuts:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-shortcuts.ps1
```

Keep the executable in its original location after creating shortcuts, or recreate the shortcuts after moving it.

## Features

- **Overview** suggests a next step from sustained CPU/memory pressure or low space, with separate actions to review apps, explore storage, and review startup. The sidebar keeps each task on its own screen.
- Live disk read/write throughput across physical disks, plus a rolling minute of CPU, memory and disk charts. Disk counters need two samples; unavailable counters are labelled rather than shown as zero. Throughput is not a saturation or disk-health measurement.
- **Storage explorer** measures your user folder, a chosen folder, or a drive. View top-level folder totals and the 50 largest files; inspect a selected folder or open its containing folder in Explorer. It is read-only, cancellable, skips links, and caps traversal at 200,000 entries. Skips and scan limits are labelled as partial totals. Sizes are logical file sizes, not unique allocated disk space; hard links and cloud placeholders can differ. Scanning can temporarily increase disk activity.
- **Startup review** lists readable Run/RunOnce registrations and current/shared startup-folder entries, and gives a three-step review guide. Open Windows Startup apps to change the available switches. The inventory does not measure startup impact or infer enabled state, and does not cover packaged-app registrations, services or scheduled tasks. Commands are displayed without executing them; this app does not edit startup registrations.
- Live CPU utilization and RAM usage; available space on the Windows drive.
- **Health insights** explain current readings with green normal, amber attention, red high, and blue observing/unavailable labels. CPU at 85%+ or RAM at 90%+ must stay elevated across samples for 20 seconds before an alert is shown. CPU/RAM at 95%+ are marked high after that period. Storage uses 10%/10 GiB attention and 5%/2 GiB high thresholds. These are guidance thresholds, not a diagnosis.
- **Live apps** lists accessible processes, measured CPU usage between samples, and working-set memory. Search by name, sort by CPU or memory, select a process for guidance, and open Task Manager to investigate. Protected or exited processes are counted as unavailable; new processes need a second sample for CPU. Working sets can include shared pages and should not be added together. A process using 20%+ CPU or a working set of 1 GiB+ is highlighted as an observation, not automatically a problem.
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

**Cleanup age** defaults to **Older than 7 days** and also offers **Older than 1 day** and **All ages — attempt recent files**. Age filtering checks both creation and last-write timestamps, so a recently copied file is protected even if its modification date is old. All ages removes the age restriction; close applications and use **Scan first** to review recent files before deleting them. Changing the age clears the existing preview and requires a fresh scan; cleanup reuses the policy captured by that scan.

All modes leave files in use, read-only files, changed candidates, directory links, and inaccessible entries alone. It deletes individual files; it never recursively deletes directories, so empty folders can remain. Deletion uses an exclusive Windows file handle and rechecks the resolved path and timestamps before deleting through that same handle. Scan traversal is capped at 100,000 entries; partial results are labeled. The preview explains each protected or skipped entry, and completion reports protected files separately from cleanup skips and exclusions.

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

The console checks use disposable fixtures inside `tests/fixtures`, never real user temporary files. They exercise age filtering, nested files, exclusive locks, changed files, read-only files, candidate containment, selected roots, file/folder exclusions, preview state, skip reasons, cancellation, junctions, storage aggregation and scan limits, largest-file ordering, disk-counter validation, startup inventory, sustained-alert thresholds, process sampling, and real read-only hardware queries.

```powershell
dotnet run --project .\tests\EngineTests.csproj -c Release -p:RestoreConfigFile="$PWD\NuGet.Config"
```

For an interface smoke test, launch the executable with `--ui-smoke-test` and an output PNG path. It opens the window, reads information, performs read-only temporary-file and storage scans, verifies age-selection changes and checkbox exclusion/reinclusion using an in-memory UI fixture, captures all eight screens, and closes without invoking cleanup or changing startup registrations. A `.result.txt` file reports the outcome.

Windows integrations follow Microsoft's [performance-counter API](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcountervalue) and [Run/RunOnce registration documentation](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys).

Later additions: opt-in browser-cache cleanup, schedules, system tray access, and theme selection.
