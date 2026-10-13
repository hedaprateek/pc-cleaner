using System.IO;

namespace PcCleaner;

public record StorageFolder(string Name, string Path, long Bytes, int Files, double Share)
{
    public string Size => SystemInformation.FormatBytes(Bytes);
    public string Summary => $"{Files:N0} files · {Share:F1}% of measured size";
}
public record LargeFile(string Name, string Path, long Bytes)
{
    public string Size => SystemInformation.FormatBytes(Bytes);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? Path;
}
public record StorageAnalysis(string Root, List<StorageFolder> Folders, List<LargeFile> LargestFiles, long Bytes, int Files, int Skipped, bool Limited);

public sealed class StorageAnalyzer
{
    public StorageAnalysis Analyze(string path, CancellationToken token, IProgress<string>? progress = null, int maximumEntries = 200_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!IsReadableUnlinkedDirectory(root)) throw new IOException("Choose an existing folder that is not a link.");
        var folders = new Dictionary<string, (string Path, long Bytes, int Files)>(StringComparer.OrdinalIgnoreCase);
        var largest = new PriorityQueue<LargeFile, long>();
        var stack = new Stack<(string Path, string Group)>(); stack.Push((root, root));
        long bytes = 0; int files = 0, skipped = 0, inspected = 0; bool limited = false;
        while (stack.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!IsReadableUnlinkedDirectory(current.Path)) { skipped++; continue; }
                foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (++inspected > maximumEntries) { limited = true; break; }
                    if (inspected % 200 == 0) progress?.Report($"{files:N0} files measured · {SystemInformation.FormatBytes(bytes)} · {skipped:N0} skips");
                    try
                    {
                        if (!Path.GetFullPath(entry).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var group = current.Path == root ? entry : current.Group;
                            folders.TryAdd(group, (group, 0, 0));
                            stack.Push((entry, group)); continue;
                        }
                        var size = new FileInfo(entry).Length;
                        var aggregate = folders.GetValueOrDefault(current.Group, (root, 0L, 0));
                        folders[current.Group] = (aggregate.Item1, aggregate.Item2 + size, aggregate.Item3 + 1);
                        files++; bytes += size;
                        largest.Enqueue(new(Path.GetFileName(entry), entry, size), size);
                        if (largest.Count > 50) largest.Dequeue();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
            if (limited) break;
        }
        var groups = folders.Select(p => new StorageFolder(p.Key == root ? "Files in this folder" : Path.GetFileName(p.Key), p.Value.Path, p.Value.Bytes, p.Value.Files, bytes == 0 ? 0 : 100.0 * p.Value.Bytes / bytes))
            .OrderByDescending(f => f.Bytes).ThenBy(f => f.Name).ToList();
        return new(root, groups, largest.UnorderedItems.Select(i => i.Element).OrderByDescending(f => f.Bytes).ThenBy(f => f.Path).ToList(), bytes, files, skipped, limited);
    }
    private static bool IsReadableUnlinkedDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists) return false;
            for (var current = directory; current != null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
