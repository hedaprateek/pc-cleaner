using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcCleaner;

public record CleanupRoot(string Name, string Path);
public record Candidate(string Category, string Root, string Path, long Bytes, DateTime ModifiedUtc, DateTime CreatedUtc);
public record FileFeedback(string Path, string Category, string Status, string Reason, long Bytes = 0, DateTime? ModifiedUtc = null)
{
    public string Size => SystemInformation.FormatBytes(Bytes);
    public string Modified => ModifiedUtc?.ToLocalTime().ToString("g") ?? "Not available";
    public string Color => Status switch { "Eligible" or "Deleted" => "#187047", "Skipped" or "Protected" => "#946200", "Failed" => "#B42318", _ => "#63718A" };
}
public record ScanResult(List<Candidate> Files, int Skipped, bool Limited)
{
    public long Bytes => Files.Sum(f => f.Bytes);
    public List<FileFeedback> Entries { get; init; } = [];
    public int Inspected { get; init; }
}
public record CleanupResult(int Deleted, int Skipped, long Bytes, bool Cancelled)
{
    public List<FileFeedback> Entries { get; init; } = [];
}

public static class CleanupSelection
{
    public static bool IsExcluded(string path, IEnumerable<string> files, IEnumerable<string> folders) =>
        files.Any(f => string.Equals(System.IO.Path.GetFullPath(f), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        || folders.Any(f => string.Equals(System.IO.Path.GetFullPath(f).TrimEnd('\\'), System.IO.Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || CleanupEngine.Contains(f, path));
    public static ScanResult Filter(ScanResult scan, IEnumerable<string> files, IEnumerable<string> folders) =>
        scan with { Files = scan.Files.Where(f => !IsExcluded(f.Path, files, folders)).ToList() };
}

public sealed class CleanupEngine
{
    public const int MinimumAgeDays = 7;
    private const int MaximumFiles = 100_000;
    public static List<CleanupRoot> DefaultRoots(bool systemTemp)
    {
        var roots = new List<CleanupRoot>
        {
            new("Your temporary files", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"))
        };
        if (systemTemp)
            roots.Add(new("Windows temporary files", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")));
        return roots;
    }

    public ScanResult Scan(IReadOnlyList<CleanupRoot> roots, CancellationToken token, IProgress<string>? progress = null)
    {
        var files = new List<Candidate>();
        var entries = new List<FileFeedback>();
        var skipped = 0;
        var inspected = 0;
        var cutoff = DateTime.UtcNow.AddDays(-MinimumAgeDays);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in roots)
        {
            token.ThrowIfCancellationRequested();
            var root = System.IO.Path.GetFullPath(target.Path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!IsSafeDirectory(root)) { skipped++; entries.Add(new(root, target.Name, "Skipped", "Folder is missing, inaccessible, or linked. It was not scanned.")); continue; }
            progress?.Report($"Scanning {target.Name.ToLowerInvariant()}…");
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!IsSafeDirectory(directory)) { skipped++; entries.Add(new(directory, target.Name, "Skipped", "Directory is inaccessible or linked.")); continue; }
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++inspected > MaximumFiles) return new(files, skipped, true) { Entries = entries, Inspected = inspected - 1 };
                        if (inspected % 250 == 0) progress?.Report($"Scanning {target.Name.ToLowerInvariant()}... {inspected:N0} entries checked, {files.Count:N0} old files found.");
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; entries.Add(new(entry, target.Name, "Skipped", "Linked file or directory; its target is not scanned.")); continue; }
                            if ((attributes & FileAttributes.Directory) != 0) { stack.Push(entry); continue; }
                            var info = new FileInfo(entry);
                            if (info.LastWriteTimeUtc >= cutoff || info.CreationTimeUtc >= cutoff)
                            { entries.Add(new(entry, target.Name, "Protected", "Created or modified within the last 7 days.", info.Length, info.LastWriteTimeUtc)); continue; }
                            if (Contains(root, entry) && seen.Add(entry))
                            {
                                files.Add(new(target.Name, root, entry, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc));
                                var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
                                entries.Add(new(entry, target.Name, readOnly ? "Protected" : "Eligible", readOnly ? "Read-only file; it will be skipped." : "Older than 7 days. Availability and timestamps will be checked again before deletion.", info.Length, info.LastWriteTimeUtc));
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; entries.Add(new(entry, target.Name, "Skipped", e is UnauthorizedAccessException ? "Access denied while inspecting this entry." : "Entry is unavailable or changed during scanning.")); }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; entries.Add(new(directory, target.Name, "Skipped", e is UnauthorizedAccessException ? "Access denied while scanning this folder." : "Folder is unavailable or changed during scanning.")); }
            }
        }
        return new(files, skipped, false) { Entries = entries, Inspected = inspected };
    }

    public CleanupResult Clean(ScanResult scan, IReadOnlyList<CleanupRoot> allowedRoots, CancellationToken token, IProgress<string>? progress = null)
    {
        var allowed = allowedRoots.Select(r => System.IO.Path.GetFullPath(r.Path).TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int deleted = 0, skipped = 0, processed = 0;
        long bytes = 0;
        var entries = new List<FileFeedback>();
        void Skip(Candidate candidate, string reason)
        {
            skipped++; entries.Add(new(candidate.Path, candidate.Category, "Skipped", reason, candidate.Bytes, candidate.ModifiedUtc));
        }
        foreach (var file in scan.Files)
        {
            if (token.IsCancellationRequested) return new(deleted, skipped, bytes, true) { Entries = entries };
            try
            {
                if (!allowed.Contains(file.Root) || !Contains(file.Root, file.Path) || !IsSafeDirectory(System.IO.Path.GetDirectoryName(file.Path)!))
                { Skip(file, "Location is outside selected roots, inaccessible, or linked."); continue; }
                // Exclusive handle prevents deleting files currently opened by other applications.
                // Delete through this same handle after checking the actual resolved location and metadata.
                using var handle = CreateFile(file.Path, 0x10000 | 0x80, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) { Skip(file, WindowsReason(Marshal.GetLastWin32Error())); continue; }
                if (!GetFileInformationByHandle(handle, out var info)) { Skip(file, "Could not verify file information: " + WindowsReason(Marshal.GetLastWin32Error())); continue; }
                var finalPath = new StringBuilder(32768);
                var pathLength = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
                if (pathLength == 0 || pathLength >= finalPath.Capacity) { Skip(file, "Could not verify the resolved file location."); continue; }
                var resolved = finalPath.ToString();
                if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
                else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
                var modified = DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow);
                var created = DateTime.FromFileTimeUtc(((long)info.CreationHigh << 32) | info.CreationLow);
                var length = ((long)info.SizeHigh << 32) | info.SizeLow;
                var cutoff = DateTime.UtcNow.AddDays(-MinimumAgeDays);
                if (!Contains(file.Root, resolved) || (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.ReadOnly)) != 0
                    || modified >= cutoff || created >= cutoff || modified != file.ModifiedUtc || created != file.CreatedUtc || length != file.Bytes)
                { Skip(file, (info.Attributes & (uint)FileAttributes.ReadOnly) != 0 ? "Read-only file; left unchanged." : "File changed since scanning, is too recent, or its resolved location is not eligible."); continue; }
                var disposition = new FileDisposition { Delete = true };
                if (SetFileInformationByHandle(handle, 4, ref disposition, 1)) { deleted++; bytes += length; entries.Add(new(file.Path, file.Category, "Deleted", "Deleted after location, age, and availability checks.", length, modified)); }
                else Skip(file, WindowsReason(Marshal.GetLastWin32Error()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Skip(file, e is UnauthorizedAccessException ? "Access denied; left unchanged." : "File is unavailable or its path could not be verified."); }
            finally
            {
                processed++;
                if (processed % 25 == 0 || processed == scan.Files.Count)
                    progress?.Report($"Cleaning… {processed:N0} / {scan.Files.Count:N0} files");
            }
        }
        return new(deleted, skipped, bytes, false) { Entries = entries };
    }

    private static string WindowsReason(int code) => code switch
    {
        2 or 3 => "File or folder no longer exists.",
        5 => "Access denied or file is read-only; left unchanged.",
        32 or 33 => "File is in use or locked by another application; left unchanged.",
        _ => $"Windows could not complete this operation (error {code}); left unchanged."
    };

    internal static bool Contains(string root, string path) => System.IO.Path.GetFullPath(path)
        .StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSafeDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists || directory.Parent is null) return false;
            for (DirectoryInfo? current = directory; current != null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileDisposition info, uint size);
}
