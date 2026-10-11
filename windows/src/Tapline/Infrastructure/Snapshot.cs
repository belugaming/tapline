using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tapline.UI;

namespace Tapline.Infrastructure;

/// Development helper: with TAPLINE_SNAPSHOT_DIR set, Tapline renders every page of the main window
/// (and the tray panel) into PNG files and quits. Used by CI to check the UI on a real Windows
/// machine, and for screenshots. It never touches Equalizer APO's configuration or the real settings.
static class Snapshot
{
    public static string? Directory => Environment.GetEnvironmentVariable("TAPLINE_SNAPSHOT_DIR");
    public static bool IsActive => !string.IsNullOrEmpty(Directory);
    /// TAPLINE_SNAPSHOT_REAL=1 skips the demo curve, meters and spectrum.
    public static bool UsesDemoData => IsActive && Environment.GetEnvironmentVariable("TAPLINE_SNAPSHOT_REAL") == null;

    public static void Run(MainWindow window, Func<TrayPanel> makePanel, Action quit)
    {
        var dir = Directory!;
        System.IO.Directory.CreateDirectory(dir);
        var pages = Enum.GetValues<PageKind>();
        var index = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (index > 0) Capture(window, pages[index - 1], dir, index - 1);
                if (index < pages.Length)
                {
                    window.Show(pages[index]);
                    index++;
                    return;
                }
                timer.Stop();
                var panel = makePanel();
                panel.Left = 40;
                panel.Top = 40;
                panel.Show();
                panel.UpdateLayout();
                Save((FrameworkElement)panel.Content, Path.Combine(dir, "tray-panel.png"));
            }
            catch (Exception e)
            {
                File.AppendAllText(Path.Combine(dir, "snapshot-errors.txt"), e + "\n\n");
            }
            if (!timer.IsEnabled) quit();
        };
        timer.Start();
    }

    private static void Capture(MainWindow window, PageKind page, string dir, int index)
    {
        window.UpdateLayout();
        var name = $"{index}-{page.ToString().ToLowerInvariant()}";
        Save((FrameworkElement)window.Content, Path.Combine(dir, name + ".png"));
        // Long pages: the whole scrollable content too.
        if (window.CurrentPage?.Content is ScrollViewer { Content: FrameworkElement content } && content.ActualHeight > ((FrameworkElement)window.CurrentPage).ActualHeight + 4)
            Save(content, Path.Combine(dir, name + "-full.png"), Theme.WindowBackground);
    }

    private static void Save(FrameworkElement element, string path, Brush? background = null)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background ?? Theme.WindowBackground, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
