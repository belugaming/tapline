using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Tapline.Infrastructure;

namespace Tapline.Audio;

/// One app in the mixer: all audio sessions of the same executable on the current output.
sealed class AudioApp
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
    public bool IsSystemSounds { get; init; }
    internal List<AudioSessionControl> Sessions { get; } = [];

    public bool IsPlaying { get; internal set; }
    public float Volume { get; internal set; }
    public bool Muted { get; internal set; }
    public float Peak { get; internal set; }
}

/// The per-app volume mixer, built on Windows' own audio sessions (the same ones the
/// Volume Mixer shows). Windows remembers each app's volume itself.
sealed class SessionCenter
{
    private static readonly Dictionary<string, (string Name, ImageSource? Icon)> AppInfoCache = [];
    private AudioSessionManager? manager;
    private string? deviceId;

    public IReadOnlyList<AudioApp> Apps { get; private set; } = [];

    /// Re-reads the sessions of `device`. Returns true if the list of apps or their state changed.
    public bool Refresh(MMDevice? device)
    {
        if (device == null)
        {
            manager = null;
            deviceId = null;
            var changed = Apps.Count > 0;
            Apps = [];
            return changed;
        }
        try
        {
            if (manager == null || deviceId != device.ID)
            {
                manager = device.AudioSessionManager;
                deviceId = device.ID;
            }
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            var apps = new Dictionary<string, AudioApp>();
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                AudioSessionState state;
                try { state = s.State; } catch { continue; }
                if (state == AudioSessionState.AudioSessionStateExpired) continue;
                var system = SafeBool(() => s.IsSystemSoundsSession);
                var pid = system ? 0 : SafeUInt(() => s.GetProcessID);
                if (!system && (pid == 0 || pid == Environment.ProcessId)) continue;
                var path = system ? null : Native.ProcessPath(pid);
                var key = system ? "system" : (path?.ToLowerInvariant() ?? $"pid:{pid}");
                if (!apps.TryGetValue(key, out var app))
                {
                    var (name, icon) = system ? (L.T("System sounds"), null) : Describe(path, pid, SafeString(() => s.DisplayName));
                    app = new AudioApp { Key = key, Name = name, Icon = icon, IsSystemSounds = system };
                    apps[key] = app;
                }
                app.Sessions.Add(s);
                if (state == AudioSessionState.AudioSessionStateActive) app.IsPlaying = true;
            }
            foreach (var app in apps.Values)
            {
                var first = app.Sessions[0];
                app.Volume = SafeFloat(() => first.SimpleAudioVolume.Volume, 1);
                app.Muted = SafeBool(() => first.SimpleAudioVolume.Mute);
            }
            var list = apps.Values
                .OrderBy(a => a.IsSystemSounds)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var signature = string.Join("|", list.Select(a => $"{a.Key}:{a.IsPlaying}:{a.Volume:F3}:{a.Muted}"));
            var old = string.Join("|", Apps.Select(a => $"{a.Key}:{a.IsPlaying}:{a.Volume:F3}:{a.Muted}"));
            Apps = list;
            return signature != old;
        }
        catch
        {
            manager = null;
            return false;
        }
    }

    /// Updates the peak level of every app (call ~30 times a second while the mixer is visible).
    public void UpdatePeaks()
    {
        foreach (var app in Apps)
        {
            float peak = 0;
            foreach (var s in app.Sessions)
                peak = Math.Max(peak, SafeFloat(() => s.AudioMeterInformation.MasterPeakValue, 0));
            app.Peak = Math.Max(peak, app.Peak * 0.85f);
        }
    }

    public void SetVolume(AudioApp app, float volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        foreach (var s in app.Sessions)
        {
            try { s.SimpleAudioVolume.Volume = volume; } catch { }
        }
        app.Volume = volume;
    }

    public void SetMuted(AudioApp app, bool muted)
    {
        foreach (var s in app.Sessions)
        {
            try { s.SimpleAudioVolume.Mute = muted; } catch { }
        }
        app.Muted = muted;
    }

    private static (string, ImageSource?) Describe(string? path, uint pid, string displayName)
    {
        var cacheKey = path ?? $"pid:{pid}";
        if (AppInfoCache.TryGetValue(cacheKey, out var cached)) return cached;
        string name = "";
        ImageSource? icon = null;
        if (path != null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription! : (info.ProductName ?? "");
            }
            catch { }
            icon = ExtractIcon(path);
        }
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith('@')) name = displayName;
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { name = path != null ? Path.GetFileNameWithoutExtension(path) : $"PID {pid}"; }
        }
        var result = (name.Trim(), icon);
        AppInfoCache[cacheKey] = result;
        return result;
    }

    private static ImageSource? ExtractIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    private static uint SafeUInt(Func<uint> f) { try { return f(); } catch { return 0; } }
    private static float SafeFloat(Func<float> f, float fallback) { try { return f(); } catch { return fallback; } }
    private static string SafeString(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
}
