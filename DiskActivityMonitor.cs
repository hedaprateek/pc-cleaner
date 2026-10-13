using System.Runtime.InteropServices;

namespace PcCleaner;

public record DiskActivity(double? ReadBytesPerSecond, double? WriteBytesPerSecond, string Status)
{
    public bool Available => ReadBytesPerSecond.HasValue && WriteBytesPerSecond.HasValue;
    public double TotalBytesPerSecond => (ReadBytesPerSecond ?? 0) + (WriteBytesPerSecond ?? 0);
    public string Read => ReadBytesPerSecond.HasValue ? SystemInformation.FormatBytes((long)ReadBytesPerSecond.Value) + "/s" : "Unavailable";
    public string Write => WriteBytesPerSecond.HasValue ? SystemInformation.FormatBytes((long)WriteBytesPerSecond.Value) + "/s" : "Unavailable";
}

// Windows PDH counters measure physical-disk throughput, across all disks.
public sealed class DiskActivityMonitor : IDisposable
{
    private IntPtr query, read, write;
    private bool initialized, baseline, disposed;
    public DiskActivity Sample()
    {
        if (disposed) return new(null, null, "Monitoring stopped");
        if (!initialized)
        {
            initialized = true;
            if (PdhOpenQuery(null, UIntPtr.Zero, out query) != 0
                || PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", UIntPtr.Zero, out read) != 0
                || PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", UIntPtr.Zero, out write) != 0)
            { Close(); return new(null, null, "Disk counters unavailable"); }
        }
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return new(null, null, "Disk counters unavailable");
        if (!baseline) { baseline = true; return new(null, null, "Waiting for a second sample"); }
        var r = ReadValue(read); var w = ReadValue(write);
        return new(r, w, r.HasValue && w.HasValue ? "Live · all physical disks" : "Disk counters unavailable");
    }
    internal static double? ValidValue(uint status, double value) => status <= 1 && double.IsFinite(value) && value >= 0 ? value : null;
    private static double? ReadValue(IntPtr counter) => PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) == 0 ? ValidValue(value.Status, value.Value) : null;
    private void Close() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = IntPtr.Zero; }
    public void Dispose() { disposed = true; Close(); }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct CounterValue
    { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")] private static extern uint PdhOpenQuery(string? source, UIntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
