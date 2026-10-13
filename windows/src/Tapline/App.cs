using System.Windows;
using System.Windows.Threading;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI;

namespace Tapline;

sealed class App : Application
{
    private SingleInstance? instance;
    private AppModel? model;
    private MainWindow? window;
    private TrayIcon? tray;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--uninstall"))
        {
            Uninstall();
            return 0;
        }
        var app = new App();
        if (!Snapshot.IsActive)
        {
            app.instance = SingleInstance.Acquire(() => app.Dispatcher.BeginInvoke(() => app.window?.BringToFront()),
                                                  () => app.Dispatcher.BeginInvoke(() => app.Quit()));
            if (app.instance == null) return 0;
        }
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml"),
        });
        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        app.Startup += (_, _) => app.Start(args.Contains(LaunchAtLogin.BackgroundArgument));
        // Signing out, shutting down or an update closing Tapline: changes from the last half second are saved too.
        app.SessionEnding += (_, _) => app.model?.SaveNow();
        return app.Run();
    }

    private void Start(bool background)
    {
        model = new AppModel(Dispatcher);
        window = new MainWindow(model);
        MainWindow = window;
        tray = new TrayIcon(model, window, Quit);
        if (Snapshot.IsActive)
        {
            window.Show();
            Snapshot.Run(window, () => new TrayPanel(model, () => { }), Quit);
            return;
        }
        if (!background) window.Show();
    }

    /// Run by the uninstaller: gives Equalizer APO back the configuration Tapline replaced
    /// (or an empty one) and removes the login item.
    private static void Uninstall()
    {
        try { LaunchAtLogin.Enabled = false; } catch { }
        try
        {
            if (!Audio.EqualizerApo.ConfigIsManaged) return;
            if (!Audio.EqualizerApo.RestoreBackup()) Audio.EqualizerApo.Write(Core.ApoConfig.Build(false, []));
            // Virtual Speakers' impulse responses next to the configuration.
            if (Audio.EqualizerApo.ConfigPath is string config)
            {
                var folder = System.IO.Path.Combine(config, Core.VirtualSpeakerFiles.Folder);
                if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true);
                var parent = System.IO.Path.GetDirectoryName(folder);
                if (parent != null && System.IO.Directory.Exists(parent) && !System.IO.Directory.EnumerateFileSystemEntries(parent).Any())
                    System.IO.Directory.Delete(parent);
            }
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
        }
    }

    public void Quit()
    {
        tray?.Dispose();
        if (window != null)
        {
            window.AllowClose = true;
            window.Close();
        }
        model?.Shutdown();
        instance?.Dispose();
        Shutdown();
    }
}

static class CrashLog
{
    public static void Write(Exception? e)
    {
        if (e == null) return;
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.Data, "errors.log"), $"{DateTime.Now:O} {e}\n\n");
        }
        catch
        {
            // Nowhere to report it.
        }
    }
}
