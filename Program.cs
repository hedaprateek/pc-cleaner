using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcCleaner;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "PC Companion", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        var smokeTest = args.Length == 2 && args[0] == "--ui-smoke-test";
        var window = new MainWindow(smokeTest);
        if (smokeTest)
        {
            window.ShowActivated = false;
            window.Loaded += async (_, _) =>
            {
                await Task.Delay(10000);
                try
                {
                    await window.ReadOnlySmokeTest();
                    var target = Path.GetFullPath(args[1]);
                    foreach (var tab in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
                    {
                        window.MainTabs.SelectedIndex = tab;
                        await Task.Delay(250);
                        window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        string[] names = ["overview", "cleanup", "apps", "storage", "startup", "insights", "computer", "history", "getting-started"];
                        var path = tab == 0 ? target : Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + "-" + names[tab] + ".png");
                        using var output = File.Create(path); encoder.Save(output);
                    }
                    // Exercise the smaller display used by hosted Windows runners locally too.
                    window.MinWidth = Math.Min(window.MinWidth, 1024); window.MinHeight = Math.Min(window.MinHeight, 720);
                    window.Width = window.MinWidth; window.Height = window.MinHeight;
                    foreach (var tab in new[] { 0, 1, 3, 4 })
                    {
                        window.MainTabs.SelectedIndex = tab; await Task.Delay(150); window.UpdateLayout();
                        if ((tab == 1 && window.ScanGrid.ActualHeight < 120)
                            || (tab == 3 && window.LargeFilesGrid.ActualHeight < 120)
                            || (tab == 4 && window.StartupGrid.ActualHeight < 120))
                            throw new InvalidOperationException($"Compact layout leaves too little room for results on tab {tab} at {window.ActualWidth}×{window.ActualHeight}.");
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        var path = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + $"-compact-{tab}.png");
                        using var output = File.Create(path); encoder.Save(output);
                    }
                    File.WriteAllText(target + ".result.txt", "PASS: First-run guide and Help navigation, overview charts, read-only storage analysis, startup inventory, cleanup-age selections and preview invalidation, preview exclusions/reinclusion, filters, insights, and process sampling. No cleanup invoked; guide preferences unchanged.");
                }
                catch (Exception ex) { File.WriteAllText(Path.GetFullPath(args[1]) + ".result.txt", "FAIL: " + ex); Environment.ExitCode = 1; }
                finally { window.Close(); }
            };
        }
        app.Run(window);
    }
}
