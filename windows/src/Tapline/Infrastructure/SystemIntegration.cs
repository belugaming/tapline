using System.IO;
using System.Reflection;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Tapline.Infrastructure;

/// One Tapline at a time. A second launch of the same build asks the first one to show its window
/// and exits; a different build (an update, or another copy of the portable app) asks it to quit
/// and takes over once it has saved its settings.
sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Tapline.SingleInstance";
    private const string ShowEventName = @"Local\Tapline.ShowWindow";
    private const string QuitEventName = @"Local\Tapline.Quit";
    private readonly Mutex mutex;
    private readonly EventWaitHandle showEvent, quitEvent, buildMarker;
    private readonly RegisteredWaitHandle showWait, quitWait;

    /// Exists while this build runs, so a second launch can tell whether it is the same build.
    private static string BuildMarkerName
    {
        get
        {
            var version = typeof(SingleInstance).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
            return @"Local\Tapline.Build." + version.Replace('\\', '_');
        }
    }

    private SingleInstance(Mutex mutex, Action onShow, Action onQuit)
    {
        this.mutex = mutex;
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        buildMarker = new EventWaitHandle(false, EventResetMode.ManualReset, BuildMarkerName);
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => onShow(), null, Timeout.Infinite, false);
        quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => onQuit(), null, Timeout.Infinite, true);
    }

    /// Returns null if another instance is running and stays (after signalling it to show itself).
    public static SingleInstance? Acquire(Action onShow, Action onQuit)
    {
        var mutex = new Mutex(true, MutexName, out var created);
        if (!created && !TakeOver(mutex))
        {
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName)) show.Set();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, onShow, onQuit);
    }

    /// Another copy is running. If it is a different build, asks it to quit and waits until it has.
    private static bool TakeOver(Mutex mutex)
    {
        if (EventWaitHandle.TryOpenExisting(BuildMarkerName, out var sameBuild))
        {
            sameBuild.Dispose();
            return false;
        }
        // Copies from before 0.1.0-beta.2 don't listen for this: show that one instead.
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit)) return false;
        using (quit) quit.Set();
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            // It ended without letting go (killed, or crashed while quitting): the mutex is ours now.
            return true;
        }
    }

    public void Dispose()
    {
        showWait.Unregister(null);
        quitWait.Unregister(null);
        showEvent.Dispose();
        quitEvent.Dispose();
        buildMarker.Dispose();
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
    }
}

/// System-wide shortcuts (RegisterHotKey) delivered through a hidden message window.
sealed class HotKeys : IDisposable
{
    private readonly HwndSource source;
    private readonly Dictionary<int, Action> actions = [];

    public HotKeys()
    {
        // A message-only window (parent HWND_MESSAGE).
        source = new HwndSource(new HwndSourceParameters("TaplineHotKeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        source.AddHook(Hook);
    }

    /// Registers Ctrl+Alt+Shift+`key`. Returns false if another app already owns it.
    public bool Register(int id, System.Windows.Input.Key key, Action action)
    {
        var vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        actions[id] = action;
        return Native.RegisterHotKey(source.Handle, id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, vk);
    }

    public void UnregisterAll()
    {
        foreach (var id in actions.Keys) Native.UnregisterHotKey(source.Handle, id);
        actions.Clear();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        source.Dispose();
    }
}

/// "Open Tapline at login" through the per-user Run key.
static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Tapline";
    public const string BackgroundArgument = "--background";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\" {BackgroundArgument}");
            else key.DeleteValue(Name, false);
        }
    }
}

/// Where Tapline keeps its files.
static class AppPaths
{
    public static string Data
    {
        get
        {
            // Snapshot runs keep their own state so they never touch the user's.
            var dir = Snapshot.IsActive
                ? Path.Combine(Snapshot.Directory!, "data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tapline");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Cache
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tapline", "Cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string State => Path.Combine(Data, "state.json");
}
