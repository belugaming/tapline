using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tapline.Core;
using Tapline.Infrastructure;

namespace Tapline.Model;

/// Everything Tapline remembers, saved as JSON in %APPDATA%\Tapline\state.json.
sealed class PersistedState
{
    public bool PowerOn { get; set; } = true;
    public Dictionary<string, DspSettings> Profiles { get; set; } = [];
    /// Names of devices that have a profile, so the configuration can label devices that are unplugged.
    public Dictionary<string, string> DeviceNames { get; set; } = [];
    public Dictionary<string, int> DeviceChannels { get; set; } = [];
    public List<EqPreset> UserPresets { get; set; } = [];
    public bool GlobalShortcuts { get; set; } = true;
    public double AnalyzerTilt { get; set; } = 3;
    public bool CloseToTray { get; set; } = true;
    /// The user agreed that Tapline replaces Equalizer APO's configuration.
    public bool TookOverApoConfig { get; set; }
    /// Small per-view preferences (like @AppStorage on macOS).
    public Dictionary<string, JsonNode?> Ui { get; set; } = [];
}

static class StateStore
{
    public static PersistedState Load()
    {
        try
        {
            if (File.Exists(AppPaths.State))
                return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(AppPaths.State), TaplineJson.Options) ?? new PersistedState();
        }
        catch
        {
            // A damaged file: keep a copy for inspection and start fresh.
            try { File.Copy(AppPaths.State, AppPaths.State + ".damaged", overwrite: true); } catch { }
        }
        return new PersistedState();
    }

    public static void Save(PersistedState state)
    {
        var temp = AppPaths.State + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, TaplineJson.Options));
        File.Move(temp, AppPaths.State, overwrite: true);
    }
}

/// Typed access to `PersistedState.Ui`.
sealed class Prefs(PersistedState state, Action changed)
{
    public T Get<T>(string key, T fallback)
    {
        try
        {
            return state.Ui.TryGetValue(key, out var node) && node != null ? node.Deserialize<T>(TaplineJson.Options) ?? fallback : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public void Set<T>(string key, T value)
    {
        state.Ui[key] = JsonSerializer.SerializeToNode(value, TaplineJson.Options);
        changed();
    }
}
