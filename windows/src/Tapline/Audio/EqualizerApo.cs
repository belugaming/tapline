using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Tapline.Core;

namespace Tapline.Audio;

/// Equalizer APO is the audio engine on Windows: an audio processing object that Windows loads
/// into the playback path of each device it is installed on. Tapline writes its configuration file;
/// Equalizer APO notices the change and applies it within a few milliseconds.
static class EqualizerApo
{
    public const string DownloadPage = "https://sourceforge.net/projects/equalizerapo/";
    public const string HeaderLine = ApoConfig.HeaderLine;
    public const string BackupName = ApoConfig.BackupName;

    private const string AppKey = @"SOFTWARE\EqualizerAPO";
    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string DisableEnhancementsValue = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

    private static RegistryKey? Open(string path)
    {
        try { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path); }
        catch { return null; }
    }

    public static string? InstallPath
    {
        get
        {
            using var key = Open(AppKey);
            return key?.GetValue("InstallPath") as string;
        }
    }

    public static string? ConfigPath
    {
        get
        {
            using var key = Open(AppKey);
            var path = key?.GetValue("ConfigPath") as string;
            if (string.IsNullOrEmpty(path) && key?.GetValue("InstallPath") is string install) path = Path.Combine(install, "config");
            return path;
        }
    }

    public static bool IsInstalled => ConfigPath is string p && Directory.Exists(p);

    public static string? ConfigFile => ConfigPath is string p ? Path.Combine(p, "config.txt") : null;

    /// Whether Equalizer APO's Configurator was used to install it on this output.
    public static bool IsInstalledOn(AudioDevice device)
    {
        using var key = Open($@"{AppKey}\Child APOs\{device.Guid}");
        if (key != null) return true;
        // Installations made by Equalizer APO 1.0 store the device as a value instead.
        using var parent = Open($@"{AppKey}\Child APOs");
        return parent?.GetValue(device.Guid) != null;
    }

    /// "Audio enhancements" switched off for the device: Windows then skips Equalizer APO too.
    public static bool EnhancementsDisabled(AudioDevice device)
    {
        using var key = Open($@"{RenderKey}\{device.Guid}\FxProperties");
        return key?.GetValue(DisableEnhancementsValue) is int v && v != 0;
    }

    /// The Configurator ("Device Selector"), where the user picks the devices Equalizer APO runs on.
    public static string? ConfiguratorPath
    {
        get
        {
            if (InstallPath is not string install) return null;
            foreach (var name in new[] { "DeviceSelector.exe", "Configurator.exe" })
            {
                var path = Path.Combine(install, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }

    public static bool OpenConfigurator()
    {
        if (ConfiguratorPath is not string path) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// Whether config.txt is currently Tapline's.
    public static bool ConfigIsManaged
    {
        get
        {
            try
            {
                return ConfigFile is string f && File.Exists(f) && File.ReadLines(f).FirstOrDefault()?.StartsWith(HeaderLine, StringComparison.Ordinal) == true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// Whether there is a configuration that is not Tapline's (the user's own, or Equalizer APO's example).
    public static bool HasForeignConfig
    {
        get
        {
            try { return ConfigFile is string f && File.Exists(f) && !ConfigIsManaged; }
            catch { return false; }
        }
    }

    public static string? ReadConfig()
    {
        try { return ConfigFile is string f && File.Exists(f) ? File.ReadAllText(f) : null; }
        catch { return null; }
    }

    public static bool HasBackup => ConfigPath is string p && File.Exists(Path.Combine(p, BackupName));

    /// Writes config.txt atomically. The first time, the user's own configuration is kept as a backup.
    public static void Write(string text)
    {
        if (ConfigPath is not string dir || ConfigFile is not string file) throw new InvalidOperationException("Equalizer APO is not installed.");
        // Equalizer APO reloads on every write, which restarts its filters (and empties a
        // convolver's history): leave an unchanged configuration alone.
        if (ConfigIsManaged && ReadConfig() == text) return;
        var backup = Path.Combine(dir, BackupName);
        if (File.Exists(file) && !File.Exists(backup) && !ConfigIsManaged) File.Copy(file, backup);
        var temp = Path.Combine(dir, "config.tapline.tmp");
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        // Equalizer APO may be reading the file at this very moment: retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, file, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(15);
            }
        }
    }

    /// Puts the configuration Tapline found back in place.
    public static bool RestoreBackup()
    {
        if (ConfigPath is not string dir || ConfigFile is not string file) return false;
        var backup = Path.Combine(dir, BackupName);
        if (!File.Exists(backup)) return false;
        File.Copy(backup, file, overwrite: true);
        File.Delete(backup);
        return true;
    }

    /// Grants the Users group write access to the config folder (normally done by Equalizer APO's
    /// installer). Asks for administrator rights.
    public static bool RepairPermissions()
    {
        if (ConfigPath is not string dir) return false;
        try
        {
            var p = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{dir}\" /grant *S-1-5-32-545:(OI)(CI)M /T")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
