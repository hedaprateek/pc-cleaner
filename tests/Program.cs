using PcCleaner;

// All deletions are confined to newly generated fixtures inside this project.
var fixtureBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../fixtures"));
Directory.CreateDirectory(fixtureBase);
var root = Path.Combine(fixtureBase, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var outside = Path.Combine(root, "outside");
var temp = Path.Combine(root, "temp");
Directory.CreateDirectory(temp); Directory.CreateDirectory(outside);
var roots = new List<CleanupRoot> { new("Fixture temp", temp) };
var engine = new CleanupEngine();
var passes = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passes++;
}
string Old(string directory, string name)
{
    var path = Path.Combine(directory, name);
    File.WriteAllText(path, "Disposable test fixture");
    File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-10));
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
    return path;
}
try
{
    var old = Old(temp, "eligible.tmp");
    var fresh = Path.Combine(temp, "fresh.tmp"); File.WriteAllText(fresh, "Keep");
    var locked = Old(temp, "locked.tmp");
    var changed = Old(temp, "changed.tmp");
    var protectedFile = Old(temp, "readonly.tmp"); File.SetAttributes(protectedFile, FileAttributes.ReadOnly);
    var external = Old(outside, "external.tmp");
    var nested = Path.Combine(temp, "nested"); Directory.CreateDirectory(nested);
    var nestedOld = Old(nested, "nested.tmp");
    var newlyCreated = Old(temp, "newly-created.tmp"); File.SetCreationTimeUtc(newlyCreated, DateTime.UtcNow);
    var snapshot = engine.Scan(roots, CancellationToken.None);
    Check(snapshot.Files.Count == 5, "Only old files are eligible; nested directories are scanned");
    Check(snapshot.Entries.Any(e => e.Path == fresh && e.Status == "Protected") && snapshot.Entries.Any(e => e.Path == protectedFile && e.Reason.Contains("Read-only")), "Preview explains recent and read-only protection");
    var filtered = CleanupSelection.Filter(snapshot, [old], [nested]);
    Check(filtered.Files.All(f => f.Path != old && f.Path != nestedOld) && File.Exists(old), "File and folder exclusions filter candidates without deleting");
    Check(!CleanupSelection.IsExcluded(Path.Combine(temp, "nested-other", "test.tmp"), [], [nested]), "Folder exclusion respects directory boundaries");
    var previewRow = new PreviewItem(snapshot.Entries.First(e => e.Path == old), true);
    previewRow.Include = false;
    Check(!previewRow.Include && previewRow.Status == "Excluded", "Unchecked preview entries explain exclusion");
    previewRow.Apply(new(old, "Fixture temp", "Not processed", "Cancelled"));
    Check(!previewRow.CanSelect && previewRow.Size != SystemInformation.FormatBytes(0), "Cancelled entries retain file metadata and cannot be selected");
    File.AppendAllText(changed, "Changed after scan");
    using (var held = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var result = engine.Clean(snapshot, roots, CancellationToken.None);
        Check(result.Deleted == 2 && result.Skipped == 3, "Locked, changed, and read-only files are skipped");
        Check(result.Entries.Any(e => e.Path == locked && e.Reason.Contains("in use")) && result.Entries.Any(e => e.Path == changed && e.Reason.Contains("changed")), "Cleanup reports locked and changed-file reasons");
        Check(!File.Exists(old) && !File.Exists(nestedOld), "Eligible files are deleted");
        Check(File.Exists(fresh) && File.Exists(newlyCreated) && File.Exists(external), "Recent and outside files remain");
    }
    var forged = new Candidate("Fixture temp", temp, external, new FileInfo(external).Length, File.GetLastWriteTimeUtc(external), File.GetCreationTimeUtc(external));
    Check(engine.Clean(new([forged], 0, false), roots, CancellationToken.None).Deleted == 0 && File.Exists(external), "Forged candidate outside root is rejected");
    var excluded = engine.Scan(roots, CancellationToken.None);
    Check(engine.Clean(excluded, [], CancellationToken.None).Deleted == 0, "Deselected roots cannot be cleaned");
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    Check(engine.Clean(excluded, roots, cancel.Token).Cancelled, "Cancelled cleanup stops before deletion");
    try { engine.Scan(roots, cancel.Token); throw new Exception("Scan ignored cancellation"); }
    catch (OperationCanceledException) { Check(true, "Scan cancellation is honored"); }
    var junction = Path.Combine(temp, "linked");
    var command = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    command.ArgumentList.Add("/c"); command.ArgumentList.Add("mklink"); command.ArgumentList.Add("/J"); command.ArgumentList.Add(junction); command.ArgumentList.Add(outside);
    using (var process = System.Diagnostics.Process.Start(command)!)
    {
        process.WaitForExit();
        Check(process.ExitCode == 0, "Created junction fixture");
    }
    var junctionScan = engine.Scan(roots, CancellationToken.None);
    Check(junctionScan.Files.All(f => !f.Path.StartsWith(junction, StringComparison.OrdinalIgnoreCase)), "Junction directories are not traversed");
    Check(engine.Scan([new("Linked root", junction)], CancellationToken.None).Files.Count == 0, "Linked roots are rejected");
    var linkCandidate = new Candidate("Fixture temp", temp, Path.Combine(junction, "external.tmp"), new FileInfo(external).Length, File.GetLastWriteTimeUtc(external), File.GetCreationTimeUtc(external));
    Check(engine.Clean(new([linkCandidate], 0, false), roots, CancellationToken.None).Deleted == 0, "Candidate beneath a junction cannot be deleted");
    Directory.Delete(junction); // Remove only the junction itself; never recursively traverse it.
    var systemInfo = new SystemInformation(); var metrics = systemInfo.ReadMetrics();
    Check(metrics.TotalMemory > 0 && metrics.FreeMemory <= metrics.TotalMemory, "Live RAM information is available");
    Check(SystemInformation.ReadDrives().Count > 0, "Drive information is available");
    var evaluator = new PerformanceInsights();
    var now = DateTimeOffset.UtcNow;
    var roomy = new DriveDetail("C:\\", "200 GiB", "50 GiB", 75) { FreeBytes = 50L * 1024 * 1024 * 1024 };
    var highUsage = new Metrics(97, 1000, 40);
    Check(evaluator.Evaluate(highUsage, roomy, now).Take(2).All(i => i.Level == "Observing"), "Brief CPU and RAM spikes do not trigger alerts");
    Check(evaluator.Evaluate(highUsage, roomy, now.AddSeconds(19)).Take(2).All(i => i.Level == "Observing"), "Sustained alert waits the full observation period");
    Check(evaluator.Evaluate(highUsage, roomy, now.AddSeconds(20)).Take(2).All(i => i.Level == "High"), "Sustained high CPU and RAM readings trigger explained alerts");
    Check(evaluator.Evaluate(new(30, 1000, 400), roomy, now.AddSeconds(22)).All(i => i.Level == "Normal"), "Recovered CPU and RAM reset their sustained observations");
    Check(evaluator.Evaluate(highUsage with { CpuAvailable = false, MemoryAvailable = false }, null, now.AddSeconds(24)).All(i => i.Level == "Unavailable"), "Missing readings do not appear healthy");
    var lowSpace = roomy with { FreeBytes = 1024 * 1024 * 1024, UsedPercent = 99 };
    Check(evaluator.Evaluate(new(30, 1000, 400), lowSpace, now)[2].Level == "High", "Low disk space is flagged with current evidence");
    var processMonitor = new ProcessMonitor();
    var firstProcesses = processMonitor.Sample();
    await Task.Delay(150);
    var secondProcesses = processMonitor.Sample();
    Check(firstProcesses.Apps.All(a => a.CpuPercent == null) && secondProcesses.Apps.Any(a => a.CpuPercent.HasValue), "Process CPU uses two measured samples rather than invented initial percentages");
    Check(secondProcesses.Apps.All(a => a.CpuPercent is null or >= 0 and <= 100) && secondProcesses.Apps.Any(a => a.Id == Environment.ProcessId), "Process readings are bounded and include this running utility");
    var details = await SystemInformation.ReadDetailsAsync();
    Check(details.Any(d => d.Name == "CPU") && details.All(d => d.Name != "Hardware information"), "CPU, Windows, and hardware information loads");
    Console.WriteLine($"{passes} checks passed. No real temporary files were deleted.");
}
finally
{
    // Verify the exact fixture target is beneath the intended fixture directory before removing it.
    if (!root.StartsWith(fixtureBase.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid fixture cleanup target");
    var link = Path.Combine(temp, "linked");
    if (Directory.Exists(link)) Directory.Delete(link);
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, true);
}
