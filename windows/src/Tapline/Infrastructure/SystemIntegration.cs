using System.IO;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Tapline.Infrastructure;

/// One Tapline at a time. A second launch asks the first one to show its window and exits.
sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Tapline.SingleInstance";
    private const string EventName = @"Local\Tapline.ShowWindow";
    private readonly Mutex mutex;
    private readonly EventWaitHandle showEvent;
    private readonly RegisteredWaitHandle? wait;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent, Action onShow)
    {
        this.mutex = mutex;
        this.showEvent = showEvent;
        wait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => onShow(), null, Timeout.Infinite, false);
    }

    /// Returns null if another instance is running (after signalling it).
    public static SingleInstance? Acquire(Action onShow)
    {
        var mutex = new Mutex(true, MutexName, out var created);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!created)
        {
            showEvent.Set();
            showEvent.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, showEvent, onShow);
    }

    public void Dispose()
    {
        wait?.Unregister(null);
        showEvent.Dispose();
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
