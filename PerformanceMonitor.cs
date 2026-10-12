using System.ComponentModel;
using System.Diagnostics;

namespace PcCleaner;

public record Insight(string Area, string Level, string Evidence, string Suggestion)
{
    public string Color => Level switch { "High" => "#B42318", "Attention" => "#946200", "Normal" => "#187047", _ => "#315DD9" };
    public string Background => Level switch { "High" => "#FFF0EE", "Attention" => "#FFF7E3", "Normal" => "#ECF8F0", _ => "#EEF3FF" };
}

public sealed class PerformanceInsights
{
    private DateTimeOffset? cpuHighSince, memoryHighSince;
    public List<Insight> Evaluate(Metrics metrics, DriveDetail? drive, DateTimeOffset now)
    {
        var result = new List<Insight>();
        result.Add(Sustained("CPU", metrics.Cpu, metrics.CpuAvailable, 85, 95, ref cpuHighSince, now,
            "Check the Apps tab for sustained CPU usage. Close an app normally if you no longer need it."));
        var ram = metrics.TotalMemory == 0 ? 0 : 100.0 * (metrics.TotalMemory - metrics.FreeMemory) / metrics.TotalMemory;
        result.Add(Sustained("RAM", ram, metrics.MemoryAvailable, 90, 95, ref memoryHighSince, now,
            "Review apps using the most memory. Close unused apps or browser tabs; temporary-file cleanup does not free their memory."));
        if (drive is null) result.Add(new("Storage", "Unavailable", "System drive readings are unavailable.", "Open Windows storage settings to review your drives."));
        else
        {
            var freePercent = 100 - drive.UsedPercent;
            var level = freePercent <= 5 || drive.FreeBytes < 2L * 1024 * 1024 * 1024 ? "High"
                : freePercent <= 10 || drive.FreeBytes < 10L * 1024 * 1024 * 1024 ? "Attention" : "Normal";
            result.Add(new("Storage", level, $"{drive.Name} has {drive.Free} available ({freePercent:F1}%).",
                level == "Normal" ? "Storage has room available. Review cleanup candidates when needed."
                : "Preview old temporary files, or open Windows storage settings to review larger storage categories."));
        }
        return result;
    }
    private static Insight Sustained(string area, double percent, bool available, double warning, double high,
        ref DateTimeOffset? since, DateTimeOffset now, string suggestion)
    {
        if (!available) { since = null; return new(area, "Unavailable", "Waiting for a valid reading.", "Readings refresh automatically while the app is open."); }
        if (percent < warning) { since = null; return new(area, "Normal", $"{percent:F0}% in use in the latest sample.", "No sustained high usage detected. Short spikes can be normal."); }
        since ??= now;
        var seconds = Math.Max(0, (now - since.Value).TotalSeconds);
        return seconds < 20
            ? new(area, "Observing", $"{percent:F0}% in use; watching for sustained usage ({seconds:F0}/20 seconds).", "A brief spike is not enough to identify a performance problem.")
            : new(area, percent >= high ? "High" : "Attention", $"{percent:F0}% in use; above {warning:F0}% for {seconds:F0} seconds.", suggestion);
    }
}

public record AppUsage(int Id, string Name, double? CpuPercent, long MemoryBytes)
{
    public string Cpu => CpuPercent.HasValue ? $"{CpuPercent:F1}%" : "Sampling";
    public string Memory => SystemInformation.FormatBytes(MemoryBytes);
    public string Level => CpuPercent >= 20 ? "High CPU sample" : MemoryBytes >= 1024L * 1024 * 1024 ? "1 GiB+ working set" : CpuPercent == null ? "Sampling CPU" : "Sampled";
    public string Color => CpuPercent >= 20 ? "#B42318" : MemoryBytes >= 1024L * 1024 * 1024 ? "#946200" : "#315DD9";
}
public record AppSample(List<AppUsage> Apps, int Unavailable, DateTimeOffset Timestamp);

public sealed class ProcessMonitor
{
    private Dictionary<int, (long Started, TimeSpan Cpu, long Sampled)> previous = [];
    public AppSample Sample()
    {
        var next = new Dictionary<int, (long Started, TimeSpan Cpu, long Sampled)>();
        var apps = new List<AppUsage>(); int unavailable = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var id = process.Id;
                    if (id == 0) continue;
                    var started = process.StartTime.ToUniversalTime().Ticks;
                    var cpu = process.TotalProcessorTime;
                    var stamp = Stopwatch.GetTimestamp();
                    double? percent = null;
                    if (previous.TryGetValue(id, out var before) && before.Started == started)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(before.Sampled, stamp).TotalSeconds;
                        if (elapsed > 0 && cpu >= before.Cpu)
                            percent = Math.Clamp(100 * (cpu - before.Cpu).TotalSeconds / (elapsed * Environment.ProcessorCount), 0, 100);
                    }
                    next[id] = (started, cpu, stamp);
                    apps.Add(new(id, process.ProcessName, percent, process.WorkingSet64));
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { unavailable++; }
            }
        }
        previous = next;
        return new(apps, unavailable, DateTimeOffset.Now);
    }
}
