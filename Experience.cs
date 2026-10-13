using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Polyline = System.Windows.Shapes.Polyline;
using Microsoft.Win32;

namespace PcCleaner;

public partial class MainWindow
{
    private readonly DiskActivityMonitor diskMonitor = new();
    private DiskActivity diskActivity = new(null, null, "Waiting for baseline");
    private readonly Queue<(double? Cpu, double? Ram, double? Disk)> activity = new();
    private CancellationTokenSource? storageOperation;
    private StorageAnalysis? storageAnalysis;
    private string? selectedStoragePath;
    private bool refreshingStartup;
    private int nextActionTab = 2;
    private readonly string guidePreferencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcCompanion", "getting-started-v1.txt");
    private readonly bool onboardingSmokeTest;
    private bool ShouldShowGuide()
    {
        try
        {
            var preference = File.Exists(guidePreferencePath) ? File.ReadAllText(guidePreferencePath).Trim() : "";
            RepeatGuide.IsChecked = preference == "show";
            return preference != "completed";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }
    private void ShowGuide_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 8;
    private void GuideTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out var tab) || tab < 0 || tab > 7) return;
        MainTabs.SelectedIndex = tab;
        if (onboardingSmokeTest) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guidePreferencePath)!);
            File.WriteAllText(guidePreferencePath, RepeatGuide.IsChecked == true ? "show" : "completed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Feedback("Guide preference could not be saved. You can still use the app and reopen Help.", "Warning"); }
    }
    private void MainTabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs == null || MetricStrip == null || e.Source != MainTabs) return;
        MetricStrip.Visibility = MainTabs.SelectedIndex is 0 or 2 or 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateExperience(Metrics metrics)
    {
        diskActivity = diskMonitor.Sample();
        DiskText.Text = diskActivity.Available ? SystemInformation.FormatBytes((long)diskActivity.TotalBytesPerSecond) + "/s" : "Sampling / unavailable";
        DiskDetail.Text = $"Read {diskActivity.Read} · Write {diskActivity.Write}";
        DiskStatus.Text = diskActivity.Status;
        activity.Enqueue((metrics.CpuAvailable ? metrics.Cpu : null,
            metrics.MemoryAvailable && metrics.TotalMemory > 0 ? 100.0 * (metrics.TotalMemory - metrics.FreeMemory) / metrics.TotalMemory : null,
            diskActivity.Available ? diskActivity.TotalBytesPerSecond : null));
        while (activity.Count > 31) activity.Dequeue();
        RenderCharts();
        LiveUpdated.Text = "Updated " + DateTime.Now.ToString("T");
        var memoryPressure = insights[1].Level is "High" or "Attention";
        var cpuPressure = insights[0].Level is "High" or "Attention";
        var storagePressure = insights[2].Level is "High" or "Attention";
        nextActionTab = memoryPressure || cpuPressure ? 2 : storagePressure ? 3 : 2;
        NextActionTitle.Text = memoryPressure ? "Memory is busy. Start with your apps."
            : cpuPressure ? "Your processor is busy. Review live apps."
            : storagePressure ? "Your drive needs some breathing room."
            : "Choose a small, useful improvement.";
        NextActionDetail.Text = memoryPressure ? insights[1].Evidence + " Close unused apps or tabs after saving your work."
            : cpuPressure ? insights[0].Evidence + " Review sustained usage before closing anything."
            : storagePressure ? insights[2].Evidence + " Find large files or preview temporary-file cleanup."
            : "Explore busy apps, find large files, or review startup. There is no single cleanup button that fixes every slowdown.";
        NextActionButton.Content = nextActionTab == 3 ? "Explore storage →" : "Review apps →";
    }
    private void NextAction_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = nextActionTab;
    private void ShowStorage_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 3;
    private void ShowStartup_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 4;
    private void AppsSearch_Changed(object sender, TextChangedEventArgs e) => RenderApps();
    private void AppSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AppSelected == null) return;
        AppSelected.Text = AppsGrid.SelectedItem is AppUsage app
            ? $"{app.Name} (PID {app.Id}) · CPU {app.Cpu} · Memory {app.Memory}. If you recognize this app and no longer need it, save your work and close it normally. Use Task Manager to investigate; avoid ending Windows or security processes."
            : "Select a process for guidance. Save your work and close familiar apps normally before using Task Manager.";
    }
    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => RenderCharts();
    private void RenderCharts()
    {
        if (CpuChart == null || RamChart == null || DiskChart == null || CpuLine == null || RamLine == null || DiskLine == null) return;
        var values = activity.ToArray();
        var maxDisk = Math.Max(1, values.Select(p => p.Disk ?? 0).DefaultIfEmpty(0).Max());
        Plot(CpuChart, CpuLine, values.Select(p => p.Cpu), 100);
        Plot(RamChart, RamLine, values.Select(p => p.Ram), 100);
        Plot(DiskChart, DiskLine, values.Select(p => p.Disk), maxDisk);
        DiskChartScale.Text = values.Any(p => p.Disk.HasValue) ? $"Disk · 0–{SystemInformation.FormatBytes((long)maxDisk)}/s" : "Disk · waiting for readings";
    }
    private static void Plot(Canvas canvas, Polyline line, IEnumerable<double?> samples, double maximum)
    {
        var points = new PointCollection(); int index = 0;
        var values = samples.ToArray();
        foreach (var sample in values)
        {
            if (sample.HasValue) points.Add(new(canvas.ActualWidth * (31 - values.Length + index) / 30,
                3 + Math.Max(0, canvas.ActualHeight - 6) * (1 - Math.Clamp(sample.Value / maximum, 0, 1))));
            index++;
        }
        line.Points = points;
    }
    private void ChooseStorage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to measure", InitialDirectory = StoragePath.Text };
        if (dialog.ShowDialog(this) != true) return;
        StoragePath.Text = dialog.FolderName;
        storageAnalysis = null; selectedStoragePath = null;
        StorageFolders.ItemsSource = null; LargeFilesGrid.ItemsSource = null;
        StorageSelected.Text = "Select a folder or file to see its full location.";
        StorageSummary.Text = "Folder changed. Choose Analyze folder to measure it.";
    }
    private async void AnalyzeStorage_Click(object sender, RoutedEventArgs e) => await AnalyzeStorage(StoragePath.Text);
    private async Task AnalyzeStorage(string path, int maximumEntries = 200_000)
    {
        if (storageOperation != null) return;
        using var source = new CancellationTokenSource(); storageOperation = source;
        AnalyzeStorageButton.IsEnabled = ChooseStorageButton.IsEnabled = false; CancelStorageButton.IsEnabled = true;
        storageAnalysis = null; selectedStoragePath = null; StorageFolders.ItemsSource = null; LargeFilesGrid.ItemsSource = null;
        StorageSelected.Text = "Select a folder or file after the scan finishes.";
        StorageSummary.Text = "Measuring folder sizes… No files are deleted.";
        try
        {
            var progress = new Progress<string>(text => { if (!source.IsCancellationRequested && !closed) StorageSummary.Text = text; });
            var result = await Task.Run(() => new StorageAnalyzer().Analyze(path, source.Token, progress, maximumEntries));
            if (closed) return;
            storageAnalysis = result; selectedStoragePath = null;
            StorageFolders.ItemsSource = result.Folders; LargeFilesGrid.ItemsSource = result.LargestFiles;
            StorageSelected.Text = "Select a folder or file to see its full location.";
            StorageSummary.Text = $"{SystemInformation.FormatBytes(result.Bytes)} measured · {result.Files:N0} files · {result.Skipped:N0} skipped entries · "
                + (result.Limited ? "Entry limit reached; partial results. Analyze a smaller folder for more detail." : result.Skipped > 0 ? "Partial totals: linked or inaccessible entries were skipped." : "Scan complete. No files deleted.");
        }
        catch (OperationCanceledException) { StorageSummary.Text = "Scan cancelled. No files deleted."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { StorageSummary.Text = "Could not measure this folder: " + e.Message; }
        finally { storageOperation = null; AnalyzeStorageButton.IsEnabled = ChooseStorageButton.IsEnabled = true; CancelStorageButton.IsEnabled = false; }
    }
    private void CancelStorage_Click(object sender, RoutedEventArgs e) => storageOperation?.Cancel();
    private async void InspectStorageFolder_Click(object sender, RoutedEventArgs e)
    {
        if (storageOperation != null) return;
        if (selectedStoragePath == null) { Feedback("Select a folder or file to inspect its containing folder."); return; }
        StoragePath.Text = selectedStoragePath;
        await AnalyzeStorage(selectedStoragePath);
    }
    private void StorageFolder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StorageFolders.SelectedItem is not StorageFolder folder) return;
        LargeFilesGrid.SelectedItem = null;
        selectedStoragePath = folder.Path;
        StorageSelected.Text = folder.Path + " · " + folder.Size;
    }
    private void LargeFile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LargeFilesGrid.SelectedItem is not LargeFile file) return;
        StorageFolders.SelectedItem = null;
        selectedStoragePath = file.Folder;
        StorageSelected.Text = file.Path + " · " + file.Size + ". Opening its folder lets you review it in Explorer.";
    }
    private void OpenStorageLocation_Click(object sender, RoutedEventArgs e)
    {
        if (selectedStoragePath == null) { Feedback("Select a folder or file in the storage results first."); return; }
        try
        {
            if (!Directory.Exists(selectedStoragePath)) { Feedback("This folder no longer exists. Analyze again.", "Warning"); return; }
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            start.ArgumentList.Add(selectedStoragePath); Process.Start(start);
            Feedback("Folder opened in Explorer. Review files before moving or deleting them.");
        }
        catch (Exception ex) { Feedback("Could not open the folder: " + ex.Message, "Error"); }
    }
    private async void RefreshStartup_Click(object sender, RoutedEventArgs e) => await RefreshStartup();
    private async Task RefreshStartup()
    {
        if (refreshingStartup || closed) return;
        refreshingStartup = true; RefreshStartupButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(StartupReview.Read);
            if (closed) return;
            StartupGrid.ItemsSource = result.Entries;
            StartupSummary.Text = $"{result.Entries.Count:N0} registered entries · {result.Skipped:N0} sources unavailable";
            StartupSelected.Text = "Select an entry to review it. Commands are displayed, never executed.";
        }
        catch (Exception e) { if (!closed) StartupSummary.Text = "Startup inventory unavailable: " + e.Message; }
        finally { refreshingStartup = false; RefreshStartupButton.IsEnabled = true; }
    }
    private void StartupEntry_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StartupGrid.SelectedItem is StartupEntry entry) StartupSelected.Text = entry.Name + ": " + entry.Guidance;
    }
}
