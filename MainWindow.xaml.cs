using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace PcCleaner;

public record HistoryEntry(DateTimeOffset Timestamp, int Deleted, int Skipped, long Bytes, bool Cancelled)
{
    public string When => Timestamp.ToLocalTime().ToString("g");
    public string Size => SystemInformation.FormatBytes(Bytes);
    public string Outcome => Cancelled ? "Cancelled" : "Completed";
}
public partial class MainWindow : Window
{
    private readonly CleanupEngine engine = new();
    private readonly SystemInformation system = new();
    private readonly PerformanceInsights insightEngine = new();
    private readonly ProcessMonitor processMonitor = new();
    private readonly HashSet<string> excludedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedFolders = new(StringComparer.OrdinalIgnoreCase);
    private List<PreviewItem> preview = [];
    private AppSample? appSample;
    private List<Insight> insights = [];
    private bool samplingApps, updatingPreview, closed;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly string historyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcCompanion", "history.json");
    private List<HistoryEntry> history = [];
    private List<Detail> details = [];
    private List<DriveDetail> drives = [];
    private ScanResult? scan;
    private CancellationTokenSource? operation;
    private bool busy;
    public MainWindow(bool readOnlySmokeTest = false)
    {
        InitializeComponent();
        onboardingSmokeTest = readOnlySmokeTest;
        if (readOnlySmokeTest || ShouldShowGuide()) MainTabs.SelectedIndex = 8;
        StoragePath.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        timer.Tick += async (_, _) => { UpdateMetrics(); await RefreshApps(); };
        Loaded += async (_, _) =>
        {
            LoadHistory(); UpdateMetrics(); timer.Start(); _ = RefreshApps(); _ = RefreshStartup(); await RefreshDetails();
        };
        Closing += OnClosing;
    }
    private List<CleanupRoot> SelectedRoots() => CleanupEngine.DefaultRoots(SystemTemp.IsChecked == true)
        .Where(r => UserTemp.IsChecked == true || r.Name != "Your temporary files").ToList();
    private int SelectedMinimumAgeDays() => CleanupAge.SelectedIndex switch { 1 => 1, 2 => 0, _ => CleanupEngine.MinimumAgeDays };
    private void CleanupAge_Changed(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged can fire before the remaining XAML controls exist.
        if (CleanButton == null || ScanSummary == null || ScanGrid == null) return;
        Selection_Changed(sender, e);
        var days = SelectedMinimumAgeDays();
        CleanupPolicyText.Text = days == 0
            ? "All ages: recent temporary files can be deleted. Close applications and use Scan first to review. Locked, read-only, linked, and changed files are still skipped. Deletion is permanent."
            : $"Files must be created and last modified more than {days} day(s) ago. Locked and read-only files are skipped. Deleted files cannot be restored by this app.";
    }
    private void UpdateMetrics()
    {
        var metrics = system.ReadMetrics();
        CpuText.Text = metrics.CpuAvailable ? $"{metrics.Cpu:F0}%" : "Sampling..."; CpuBar.Value = metrics.Cpu;
        RamText.Text = metrics.MemoryAvailable ? $"{SystemInformation.FormatBytes((long)(metrics.TotalMemory - metrics.FreeMemory))} / {SystemInformation.FormatBytes((long)metrics.TotalMemory)}" : "Unavailable";
        RamBar.Value = metrics.TotalMemory == 0 ? 0 : 100.0 * (metrics.TotalMemory - metrics.FreeMemory) / metrics.TotalMemory;
        drives = SystemInformation.ReadDrives(); DrivesGrid.ItemsSource = drives;
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var systemDrive = drives.FirstOrDefault(d => d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
        StorageText.Text = systemDrive?.Free ?? "Not available";
        insights = insightEngine.Evaluate(metrics, systemDrive, DateTimeOffset.Now);
        InsightsList.ItemsSource = insights;
        CpuLevel.Text = insights[0].Level + " · " + (metrics.CpuAvailable ? "Updated " + DateTime.Now.ToString("T") : "Waiting for baseline");
        RamFree.Text = insights[1].Level + $" · {SystemInformation.FormatBytes((long)metrics.FreeMemory)} available";
        StorageLevel.Text = insights[2].Level + " · See Health insights";
        CpuLevel.Foreground = CpuBar.Foreground = Brush(insights[0].Color);
        RamFree.Foreground = RamBar.Foreground = Brush(insights[1].Color);
        StorageLevel.Foreground = Brush(insights[2].Color);
        UpdateExperience(metrics);
    }
    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private void Feedback(string message, string level = "Info")
    {
        StatusText.Text = message;
        StatusText.Foreground = Brush(level switch { "Error" => "#B42318", "Warning" => "#946200", "Success" => "#187047", _ => "#315DD9" });
    }
    private async Task RefreshApps()
    {
        if (samplingApps || closed) return;
        samplingApps = true;
        try
        {
            appSample = await Task.Run(processMonitor.Sample);
            if (!closed) RenderApps();
        }
        catch (Exception e) { if (!closed) AppsSummary.Text = "Process readings unavailable: " + e.Message; }
        finally { samplingApps = false; }
    }
    private void RenderApps()
    {
        if (appSample == null || AppsGrid == null) return;
        var selectedId = (AppsGrid.SelectedItem as AppUsage)?.Id;
        var search = AppsSearch?.Text.Trim() ?? "";
        var visible = (AppSort.SelectedIndex == 1
            ? appSample.Apps.OrderByDescending(a => a.CpuPercent ?? -1).ThenByDescending(a => a.MemoryBytes)
            : appSample.Apps.OrderByDescending(a => a.MemoryBytes)).Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        AppsGrid.ItemsSource = visible;
        if (selectedId.HasValue) AppsGrid.SelectedItem = visible.FirstOrDefault(a => a.Id == selectedId.Value);
        AppsSummary.Text = $"{visible.Count:N0} shown / {appSample.Apps.Count:N0} processes measured · {appSample.Unavailable:N0} protected or exited processes unavailable · Updated {appSample.Timestamp.ToLocalTime():T}. Refreshes every 2 seconds; new processes need another sample for CPU.";
    }
    private void AppSort_Changed(object sender, SelectionChangedEventArgs e) => RenderApps();
    private void ShowApps_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;
    private void TaskManager_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe")) { UseShellExecute = true }); Feedback("Task Manager opened. Review an app before closing it."); }
        catch (Exception ex) { Feedback("Could not open Task Manager: " + ex.Message, "Error"); }
    }
    private async Task RefreshDetails()
    {
        RefreshButton.IsEnabled = false;
        try { details = await SystemInformation.ReadDetailsAsync(); DetailsGrid.ItemsSource = details; }
        finally { RefreshButton.IsEnabled = true; }
    }
    private void Selection_Changed(object sender, RoutedEventArgs e)
    {
        scan = null; CleanButton.IsEnabled = false; preview = []; ScanGrid.ItemsSource = null;
        ScanSummary.Text = "Selection changed. Scan again or use Quick Clean.";
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunOperation(false, false);
    private async void Quick_Click(object sender, RoutedEventArgs e) => await RunOperation(true, false);
    private async void Clean_Click(object sender, RoutedEventArgs e) => await RunOperation(true, true);
    private void Cancel_Click(object sender, RoutedEventArgs e) { operation?.Cancel(); Feedback("Cancellation requested. Waiting for the current file to finish; completed deletions cannot be undone.", "Warning"); }
    private void RenderPreview()
    {
        if (scan == null) return;
        foreach (var row in preview) row.PropertyChanged -= PreviewChanged;
        preview = scan.Entries.Select(f => new PreviewItem(f, !CleanupSelection.IsExcluded(f.Path, excludedFiles, excludedFolders))).ToList();
        foreach (var row in preview) row.PropertyChanged += PreviewChanged;
        ScanGrid.ItemsSource = preview;
        ApplyPreviewFilter(); UpdatePreviewSummary();
    }
    private void PreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (updatingPreview || busy || e.PropertyName != nameof(PreviewItem.Include) || sender is not PreviewItem row) return;
        if (row.Include)
        {
            if (CleanupSelection.IsExcluded(row.Path, [], excludedFolders))
            { updatingPreview = true; row.Include = false; updatingPreview = false; Feedback("This file is inside an excluded folder. Clear folder exclusions before including it.", "Warning"); return; }
            excludedFiles.Remove(row.Path);
        }
        else excludedFiles.Add(row.Path);
        UpdatePreviewSummary(); ApplyPreviewFilter();
    }
    private ScanResult SelectedScan()
    {
        if (scan == null) return new([], 0, false);
        var selected = preview.Where(r => r.Include).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CleanupSelection.Filter(scan with { Files = scan.Files.Where(f => selected.Contains(f.Path)).ToList() }, excludedFiles, excludedFolders);
    }
    private void UpdatePreviewSummary()
    {
        var selected = SelectedScan();
        if (scan != null)
            ScanSummary.Text = $"{scan.Inspected:N0} entries checked · {selected.Files.Count:N0} selected files ({SystemInformation.FormatBytes(selected.Bytes)}) · {preview.Count(r => r.Status == "Protected"):N0} protected · {preview.Count(r => r.Status == "Excluded"):N0} excluded · {scan.Skipped:N0} scan skips." + (scan.Limited ? " Scan limit reached; preview is partial." : "");
        CleanButton.IsEnabled = !busy && selected.Files.Count > 0;
        ExclusionText.Text = $"{excludedFiles.Count:N0} file exclusions · {excludedFolders.Count:N0} folder exclusions (this session, including Quick Clean). Uncheck to keep a file. Filters only change what is shown.";
        ExclusionText.ToolTip = string.Join(Environment.NewLine, excludedFiles.Concat(excludedFolders));
    }
    private void PreviewFilter_Changed(object sender, RoutedEventArgs e) => ApplyPreviewFilter();
    internal async Task ReadOnlySmokeTest()
    {
        if (MainTabs.SelectedIndex != 8) throw new InvalidOperationException("First-run guide was not shown.");
        GuideCleanupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (MainTabs.SelectedIndex != 1 || scan != null) throw new InvalidOperationException("Guide cleanup link did not navigate without scanning.");
        GuideOverviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (MainTabs.SelectedIndex != 0) throw new InvalidOperationException("Guide overview link did not navigate.");
        ShowGuide_Click(this, new RoutedEventArgs());
        if (MainTabs.SelectedIndex != 8) throw new InvalidOperationException("Help did not reopen the guide.");
        MainTabs.SelectedIndex = 1;
        MainTabs.UpdateLayout();
        await RunOperation(false, false);
        if (scan == null) throw new InvalidOperationException("Smoke test scan did not finish.");
        var original = scan;
        var fixturePath = Path.Combine(SelectedRoots()[0].Path, "pc-companion-ui-fixture.tmp");
        var old = DateTime.UtcNow.AddDays(-10);
        scan = new([new("UI fixture", SelectedRoots()[0].Path, fixturePath, 42, old, old)], 0, false)
        { Entries = [new(fixturePath, "UI fixture", "Eligible", "Read-only UI selection test; no file is created or deleted.", 42, old)], Inspected = 1 };
        RenderPreview();
        var row = preview[0];
        try
        {
            ScanGrid.ScrollIntoView(row); ScanGrid.UpdateLayout();
            var container = ScanGrid.ItemContainerGenerator.ContainerFromItem(row);
            static CheckBox? FindCheckbox(DependencyObject parent)
            {
                if (parent is CheckBox box) return box;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                { var child = FindCheckbox(VisualTreeHelper.GetChild(parent, i)); if (child != null) return child; }
                return null;
            }
            var checkbox = container == null ? null : FindCheckbox(container);
            if (checkbox == null) throw new InvalidOperationException("Preview checkbox did not render.");
            checkbox.IsChecked = false; checkbox.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateSource();
            if (SelectedScan().Files.Any(f => f.Path == row.Path)) throw new InvalidOperationException("Excluded preview file remained selected.");
            checkbox.IsChecked = true; checkbox.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateSource();
            if (!SelectedScan().Files.Any(f => f.Path == row.Path)) throw new InvalidOperationException("Reincluded preview file was not selected.");
            PreviewFilter.SelectedIndex = 2;
            if (SelectedScan().Files.Count != 1) throw new InvalidOperationException("Display filter changed cleanup selection.");
        }
        finally { excludedFiles.Remove(fixturePath); scan = original; PreviewFilter.SelectedIndex = 0; RenderPreview(); }
        if (insights.Count != 3 || appSample == null) throw new InvalidOperationException("Performance screens did not load.");
        if (CleanupAge.SelectedIndex != 0 || scan?.MinimumAgeDays != 7) throw new InvalidOperationException("Default cleanup age changed unexpectedly.");
        CleanupAge.SelectedIndex = 1;
        if (scan != null || CleanButton.IsEnabled || preview.Count != 0) throw new InvalidOperationException("Changing cleanup age did not invalidate the preview.");
        await RunOperation(false, false);
        if (scan?.MinimumAgeDays != 1) throw new InvalidOperationException("One-day selection was not applied to scanning.");
        CleanupAge.SelectedIndex = 2;
        if (scan != null || CleanButton.IsEnabled) throw new InvalidOperationException("All-ages selection reused an old scan.");
        await RunOperation(false, false);
        if (scan?.MinimumAgeDays != 0) throw new InvalidOperationException("All-ages selection was not applied to scanning.");
        SetBusy(true);
        if (CleanupAge.IsEnabled) throw new InvalidOperationException("Cleanup age could change during an operation.");
        SetBusy(false);
        await RefreshStartup();
        if (StartupGrid.ItemsSource == null) throw new InvalidOperationException("Startup inventory did not load.");
        StoragePath.Text = CleanupEngine.DefaultRoots(false)[0].Path;
        await AnalyzeStorage(StoragePath.Text, maximumEntries: 5000);
        if (storageAnalysis == null) throw new InvalidOperationException("Storage analysis did not finish.");
        if (storageAnalysis.Folders.Count > 0)
        {
            StorageFolders.SelectedIndex = 0;
            if (selectedStoragePath != storageAnalysis.Folders[0].Path) throw new InvalidOperationException("Storage folder selection did not expose its location.");
        }
        if (storageAnalysis.LargestFiles.Count > 0)
        {
            LargeFilesGrid.SelectedIndex = 0;
            if (selectedStoragePath != storageAnalysis.LargestFiles[0].Folder || StorageFolders.SelectedItem != null) throw new InvalidOperationException("Largest-file selection did not expose its containing folder.");
        }
        if (StartupGrid.Items.Count > 0)
        {
            StartupGrid.SelectedIndex = 0;
            if (!StartupSelected.Text.Contains(((StartupEntry)StartupGrid.SelectedItem).Name)) throw new InvalidOperationException("Startup selection guidance did not render.");
        }
        var runningApp = appSample!.Apps.First(a => a.Id == Environment.ProcessId);
        AppsSearch.Text = runningApp.Name;
        if (!AppsGrid.Items.Cast<AppUsage>().All(a => a.Name.Contains(runningApp.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("App search did not filter results.");
        AppsGrid.SelectedItem = AppsGrid.Items.Cast<AppUsage>().First(a => a.Id == Environment.ProcessId);
        RenderApps();
        if ((AppsGrid.SelectedItem as AppUsage)?.Id != Environment.ProcessId) throw new InvalidOperationException("App selection was lost when readings refreshed.");
        AppsSearch.Text = "";
        if (activity.Count < 2 || CpuLine.Points.Count < 1) throw new InvalidOperationException("Live charts did not load.");
        MainTabs.SelectedIndex = 0;
    }
    private void ApplyPreviewFilter()
    {
        if (ScanGrid?.ItemsSource == null || PreviewFilter == null || PreviewSearch == null) return;
        var status = (PreviewFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All entries";
        var search = PreviewSearch.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(ScanGrid.ItemsSource);
        view.Filter = value => value is PreviewItem row && (status == "All entries" || row.Status == status) && row.Path.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
    private void ExcludeFiles_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        foreach (var row in ScanGrid.SelectedItems.Cast<PreviewItem>().Where(r => r.CanSelect).ToList())
            excludedFiles.Add(row.Path);
        ApplyExclusions(); Feedback("Selected eligible files will be kept. Exclusions also apply to Quick Clean.");
    }
    private void ExcludeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to exclude from cleanup" };
        if (dialog.ShowDialog(this) != true) return;
        excludedFolders.Add(Path.GetFullPath(dialog.FolderName)); ApplyExclusions(); Feedback("Folder excluded for this session: " + dialog.FolderName);
    }
    private void ClearExclusions_Click(object sender, RoutedEventArgs e)
    {
        excludedFiles.Clear(); excludedFolders.Clear(); ApplyExclusions(); Feedback("Session exclusions cleared. Eligible preview files are selected again.");
    }
    private void ApplyExclusions()
    {
        updatingPreview = true;
        foreach (var row in preview.Where(r => r.CanSelect)) row.Include = !CleanupSelection.IsExcluded(row.Path, excludedFiles, excludedFolders);
        updatingPreview = false; UpdatePreviewSummary(); ApplyPreviewFilter();
    }
    private async Task RunOperation(bool clean, bool useScan)
    {
        if (busy) return;
        var roots = SelectedRoots();
        var minimumAgeDays = SelectedMinimumAgeDays();
        if (roots.Count == 0) { Feedback("Select at least one cleanup category.", "Warning"); return; }
        using var tokenSource = new CancellationTokenSource(); operation = tokenSource;
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => { if (busy && !tokenSource.IsCancellationRequested) Feedback(message); });
            Feedback(useScan ? "Checking selected preview files..." : "Starting scan. No files are deleted during scanning.");
            if (!useScan || scan == null) scan = await Task.Run(() => engine.Scan(roots, tokenSource.Token, progress, minimumAgeDays));
            RenderPreview();
            if (!clean) { Feedback("Scan complete; no files deleted. Review reasons and uncheck anything you want to keep.", scan.Limited || scan.Skipped > 0 ? "Warning" : "Success"); return; }
            var selectedScan = SelectedScan();
            if (selectedScan.Files.Count == 0) { Feedback("Nothing selected for cleanup. Review protected-file reasons; choose a shorter cleanup age to include recent files, or review exclusions.", "Warning"); return; }
            var before = SystemInformation.ReadDrives();
            var result = await Task.Run(() => engine.Clean(selectedScan, roots, tokenSource.Token, progress));
            var scanSkipped = scan.Skipped;
            var scanLimited = scan.Limited;
            var kept = preview.Count(r => r.Status == "Excluded");
            var protectedCount = preview.Count(r => r.Status == "Protected");
            var results = result.Entries.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var attempted = selectedScan.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            updatingPreview = true;
            foreach (var row in preview)
                if (results.TryGetValue(row.Path, out var feedback)) row.Apply(feedback);
                else if (attempted.Contains(row.Path)) row.Apply(new(row.Path, row.Category, "Not processed", "Cleanup was cancelled before this file was processed."));
            updatingPreview = false;
            scan = null;
            history.Insert(0, new(DateTimeOffset.Now, result.Deleted, result.Skipped, result.Bytes, result.Cancelled));
            history = history.Take(100).ToList(); HistoryGrid.ItemsSource = history;
            Feedback($"{(result.Cancelled ? "Cancelled" : "Cleanup complete")} · Deleted {result.Deleted:N0} files ({SystemInformation.FormatBytes(result.Bytes)}) · {protectedCount:N0} protected files kept · {result.Skipped:N0} cleanup skips · {scanSkipped:N0} scan skips · {kept:N0} excluded files kept.", result.Cancelled || result.Skipped > 0 ? "Warning" : "Success");
            var after = SystemInformation.ReadDrives();
            var changes = after.Where(d => before.Any(b => b.Name == d.Name)).Select(d =>
            {
                var delta = d.FreeBytes - before.First(b => b.Name == d.Name).FreeBytes;
                return $"{d.Name} {(delta >= 0 ? "+" : "-")}{SystemInformation.FormatBytes(Math.Abs(delta))}";
            });
            ScanSummary.Text = $"Results remain in the preview. Available-space change: {string.Join("; ", changes)}. Other disk activity can affect this measurement." + (scanLimited ? " Scan was partial; scan again to inspect remaining entries." : " Scan again for current eligibility.");
            ApplyPreviewFilter();
            try { Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!); await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(history)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Feedback(StatusText.Text + " History could not be saved: " + e.Message, "Warning"); }
            UpdateMetrics();
        }
        catch (OperationCanceledException) { scan = null; preview = []; ScanGrid.ItemsSource = null; ScanSummary.Text = "Scan cancelled; no files deleted."; Feedback("Scan cancelled; no files deleted.", "Warning"); }
        catch (Exception e) { scan = null; Feedback("Operation failed: " + e.Message + " Review the preview before retrying.", "Error"); }
        finally { operation = null; SetBusy(false); }
    }
    private void SetBusy(bool value)
    {
        busy = value; QuickButton.IsEnabled = ScanButton.IsEnabled = UserTemp.IsEnabled = SystemTemp.IsEnabled = CleanupAge.IsEnabled = !value;
        CleanButton.IsEnabled = !value && SelectedScan().Files.Count > 0; CancelButton.IsEnabled = value;
        ScanGrid.IsEnabled = ExcludeButton.IsEnabled = FolderButton.IsEnabled = ClearExclusionsButton.IsEnabled = !value;
        WorkProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private void LoadHistory()
    {
        try { if (File.Exists(historyPath)) history = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(historyPath))?.Take(100).ToList() ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { StatusText.Text = "Previous cleanup history could not be read."; }
        HistoryGrid.ItemsSource = history;
    }
    private string Report()
    {
        var report = new StringBuilder().AppendLine("PC Companion — computer report").AppendLine(DateTimeOffset.Now.ToString("f")).AppendLine();
        foreach (var detail in details) report.AppendLine($"{detail.Name}: {detail.Value}");
        report.AppendLine();
        foreach (var drive in drives) report.AppendLine($"{drive.Name} Capacity: {drive.Capacity}; Available: {drive.Free}");
        report.AppendLine();
        foreach (var insight in insights) report.AppendLine($"{insight.Area} - {insight.Level}: {insight.Evidence} {insight.Suggestion}");
        return report.ToString();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDetails();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Report()); Feedback("Computer details and performance observations copied.", "Success"); }
        catch (Exception ex) { Feedback("Could not copy: " + ex.Message, "Error"); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "computer-details.txt", Filter = "Text report (*.txt)|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, Report()); Feedback("Computer report saved with current performance observations.", "Success"); }
        catch (Exception ex) { Feedback("Could not export: " + ex.Message, "Error"); }
    }
    private void Storage_Click(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:storagesense");
    private async void ExportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (preview.Count == 0) { Feedback("Scan first to create a preview report.", "Warning"); return; }
        var dialog = new SaveFileDialog { FileName = "cleanup-preview.csv", Filter = "CSV report (*.csv)|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "Path,Category,Size,Modified,Status,Reason" };
        lines.AddRange(preview.Select(row => string.Join(",", new[] { row.Path, row.Category, row.Size, row.Modified, row.Status, row.Reason }.Select(Quote))));
        try { await File.WriteAllLinesAsync(dialog.FileName, lines, Encoding.UTF8); Feedback($"Exported all {preview.Count:N0} entries, including filtered-out rows. The report contains local file paths.", "Success"); }
        catch (Exception ex) { Feedback("Could not export preview: " + ex.Message, "Error"); }
    }
    private void Startup_Click(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:startupapps");
    private void OpenSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); Feedback("Windows settings opened."); }
        catch (Exception e) { Feedback("Could not open Windows settings: " + e.Message, "Error"); }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; operation?.Cancel(); StatusText.Text = "Cancelling. Close again when the operation has stopped."; return; }
        if (storageOperation != null) { e.Cancel = true; storageOperation.Cancel(); Feedback("Cancelling the storage scan. Close again when it finishes."); return; }
        closed = true; timer.Stop(); diskMonitor.Dispose();
    }
}
