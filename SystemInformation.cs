using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PcCleaner;
public record Detail(string Name, string Value);
public record DriveDetail(string Name, string Capacity, string Free, double UsedPercent)
{
    public long FreeBytes { get; init; }
}
public record Metrics(double Cpu, ulong TotalMemory, ulong FreeMemory)
{
    public bool CpuAvailable { get; init; } = true;
    public bool MemoryAvailable { get; init; } = true;
}

public sealed class SystemInformation
{
    private ulong previousIdle, previousKernel, previousUser;
    public Metrics ReadMetrics()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var memoryAvailable = GlobalMemoryStatusEx(ref memory);
        var cpuAvailable = false;
        double cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user;
            var previousTotal = previousKernel + previousUser;
            if (previousTotal != 0 && total > previousTotal)
            {
                cpu = Math.Clamp(100.0 * (1 - (double)(idle - previousIdle) / (total - previousTotal)), 0, 100);
                cpuAvailable = true;
            }
            previousIdle = idle; previousKernel = kernel; previousUser = user;
        }
        return new(cpu, memory.TotalPhysical, memory.AvailablePhysical) { CpuAvailable = cpuAvailable, MemoryAvailable = memoryAvailable };
    }
    public static List<DriveDetail> ReadDrives()
    {
        var result = new List<DriveDetail>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize == 0) continue;
                result.Add(new(drive.Name, FormatBytes(drive.TotalSize), FormatBytes(drive.AvailableFreeSpace), 100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize) { FreeBytes = drive.AvailableFreeSpace });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
    public static async Task<List<Detail>> ReadDetailsAsync()
    {
        var details = new List<Detail>
        {
            new("Computer name", Environment.MachineName),
            new("Architecture", RuntimeInformation.OSArchitecture.ToString()),
            new("Uptime", TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\d\ hh\h\ mm\m"))
        };
        const string script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            "$ErrorActionPreference='Stop'; $c=Get-CimInstance Win32_ComputerSystem; $o=Get-CimInstance Win32_OperatingSystem; " +
            "$p=@(Get-CimInstance Win32_Processor); $r=@(Get-CimInstance Win32_PhysicalMemory); $g=@(Get-CimInstance Win32_VideoController); " +
            "$b=Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue; $d=@(Get-PhysicalDisk -ErrorAction SilentlyContinue); " +
            "[ordered]@{'Manufacturer'=$c.Manufacturer; 'Model'=$c.Model; 'Windows'=$o.Caption; 'Version / build'=($o.Version+' / '+$o.BuildNumber); " +
            "'CPU'=($p.Name -join ', '); 'CPU cores'=(($p | Measure-Object NumberOfCores -Sum).Sum); " +
            "'Logical processors'=(($p | Measure-Object NumberOfLogicalProcessors -Sum).Sum); " +
            "'Installed RAM'=(('{0:N1} GiB' -f (($r | Measure-Object Capacity -Sum).Sum / 1GB))); " +
            "'GPU'=($g.Name -join ', '); 'Storage devices'=(($d | ForEach-Object {($_.FriendlyName+' ('+$_.MediaType+')')}) -join ', '); 'Battery charge'=$(if($b){(($b.EstimatedChargeRemaining -join ', ')+'%')}else{'Not available'}); " +
            "'Battery status'=$(if($b){(($b | ForEach-Object {switch($_.BatteryStatus){1{'Discharging'} 2{'AC power'} 3{'Fully charged'} 6{'Charging'} 7{'Charging / high'} 8{'Charging / low'} 9{'Charging / critical'} default{'Not available'}}}) -join ', ')}else{'Not available'})} | ConvertTo-Json -Compress";
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("Hardware information took too long to load."); }
            var json = await output;
            await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("Windows hardware information is unavailable on this computer.");
            using var document = JsonDocument.Parse(json.Trim('\uFEFF', '\r', '\n', ' '));
            foreach (var property in document.RootElement.EnumerateObject())
                details.Add(new(property.Name, string.IsNullOrWhiteSpace(property.Value.ToString()) ? "Not available" : property.Value.ToString()));
        }
        catch (Exception e) { details.Add(new("Hardware information", e.Message)); }
        return details;
    }
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:N1} {units[unit]}";
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
