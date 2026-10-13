using System.IO;
using System.Security;
using Microsoft.Win32;

namespace PcCleaner;

public record StartupEntry(string Name, string Source, string Command)
{
    public string Guidance => "Keep it if you need it immediately after sign-in. Otherwise, review it in Windows Startup apps. Keep security, backup, accessibility, and device utilities you rely on.";
}
public record StartupInventory(List<StartupEntry> Entries, int Skipped);

public static class StartupReview
{
    public static StartupInventory Read()
    {
        var entries = new List<StartupEntry>(); int skipped = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
        foreach (var kind in new[] { "Run", "RunOnce" })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\" + kind, false);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var command = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (string.IsNullOrWhiteSpace(command)) continue;
                    var source = (hive == RegistryHive.CurrentUser ? "Your account" : "All users") + " · " + kind;
                    if (seen.Add($"{source}\n{name}\n{command}")) entries.Add(new(string.IsNullOrWhiteSpace(name) ? "Unnamed entry" : name, source, command));
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException) { skipped++; }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    entries.Add(new(Path.GetFileNameWithoutExtension(file), folder == Environment.SpecialFolder.Startup ? "Your startup folder" : "Shared startup folder", file));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }
}
