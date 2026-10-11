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
    private static readonly JsonNodeOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };
    // What this version could not read, written back on save (see TolerantJson): profiles and
    // presets entry by entry, and top-level settings it does not know (saved by a newer Tapline).
    private static readonly TolerantJson profiles = new(), presets = new();
    private static readonly Dictionary<string, JsonNode?> unknown = [];

    public static PersistedState Load()
    {
        if (!File.Exists(AppPaths.State)) return new PersistedState();
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(AppPaths.State), CaseInsensitive) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            root = null;
        }
        if (root == null)
        {
            // Not readable at all: keep a copy for inspection and start fresh.
            try { File.Copy(AppPaths.State, AppPaths.State + ".damaged", overwrite: true); } catch { }
            return new PersistedState();
        }

        // Setting by setting, so one value this version cannot read only resets itself.
        var state = new PersistedState();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in typeof(PersistedState).GetProperties())
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            known.Add(key);
            if (property.Name is nameof(PersistedState.Profiles) or nameof(PersistedState.UserPresets)) continue;
            if (root[key] is not { } node) continue;
            try
            {
                if (node.Deserialize(property.PropertyType, TaplineJson.Options) is { } value) property.SetValue(state, value);
            }
            catch
            {
                // The default stays.
            }
        }
        state.Profiles = profiles.DecodeObject<DspSettings>(root[nameof(PersistedState.Profiles)], TaplineJson.Options) ?? [];
        state.UserPresets = presets.DecodeArray<EqPreset>(root[nameof(PersistedState.UserPresets)], TaplineJson.Options) ?? [];
        unknown.Clear();
        foreach (var (key, value) in root)
            if (!known.Contains(key)) unknown[key] = value?.DeepClone();
        return state;
    }

    public static void Save(PersistedState state)
    {
        var root = JsonSerializer.SerializeToNode(state, TaplineJson.Options) as JsonObject ?? new JsonObject();
        root[JsonNamingPolicy.CamelCase.ConvertName(nameof(PersistedState.Profiles))] = profiles.EncodeObject(state.Profiles, TaplineJson.Options);
        root[JsonNamingPolicy.CamelCase.ConvertName(nameof(PersistedState.UserPresets))] = presets.EncodeArray(state.UserPresets, TaplineJson.Options);
        foreach (var (key, value) in unknown)
            if (!root.ContainsKey(key)) root[key] = value?.DeepClone();
        var temp = AppPaths.State + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(TaplineJson.Options));
        File.Move(temp, AppPaths.State, overwrite: true);
    }

    /// <summary>For "reset everything": the next save writes only what Tapline holds.</summary>
    public static void ForgetUnreadable()
    {
        profiles.Forget();
        presets.Forget();
        unknown.Clear();
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
