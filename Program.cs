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
        var window = new MainWindow();
        if (args.Length == 2 && args[0] == "--ui-smoke-test")
        {
            window.ShowActivated = false;
            window.Loaded += async (_, _) =>
            {
                await Task.Delay(10000);
                try
                {
                    await window.ReadOnlySmokeTest();
                    var target = Path.GetFullPath(args[1]);
                    foreach (var tab in new[] { 0, 1, 2 })
                    {
                        window.MainTabs.SelectedIndex = tab;
                        await Task.Delay(250);
                        window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        var path = tab == 0 ? target : Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + (tab == 1 ? "-insights.png" : "-apps.png"));
                        using var output = File.Create(path); encoder.Save(output);
                    }
                    File.WriteAllText(target + ".result.txt", "PASS: Read-only scan, preview exclusions/reinclusion, filters, insights, and process sampling. No cleanup invoked.");
                }
                catch (Exception ex) { File.WriteAllText(Path.GetFullPath(args[1]) + ".result.txt", "FAIL: " + ex); Environment.ExitCode = 1; }
                finally { window.Close(); }
            };
        }
        app.Run(window);
    }
}
