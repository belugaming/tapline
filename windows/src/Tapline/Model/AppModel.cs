using System.IO;
using System.Windows.Threading;
using Tapline.Audio;
using Tapline.Core;
using Tapline.Core.Services;
using Tapline.Infrastructure;

namespace Tapline.Model;

enum EngineStatus
{
    /// Processing switched off by the user: audio plays unprocessed.
    Off,
    /// Equalizer APO is not installed.
    NeedsEqualizerApo,
    /// Equalizer APO has a configuration of its own; Tapline waits for the user's go-ahead to replace it.
    NeedsTakeover,
    /// Equalizer APO is installed, but not on the current output device.
    NotOnDevice,
    /// Windows' "Audio enhancements" are off for the device, which also disables Equalizer APO.
    EnhancementsDisabled,
    /// Tapline could not write Equalizer APO's configuration.
    WriteFailed,
    NoOutput,
    Running,
}

enum CompareSlot { A, B }

/// The app's state: per-device EQ profiles, presets, undo, A/B, and the link to Equalizer APO.
/// Lives on the UI thread. `Changed` fires (coalesced) after any change.
sealed class AppModel
{
    private readonly Dispatcher dispatcher;
    private readonly PersistedState state;
    private readonly Dictionary<string, DspSettings> alternate = [];
    private readonly List<DspSettings> undoStack = [];
    private readonly List<DspSettings> redoStack = [];
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer configTimer;
    private readonly DispatcherTimer meterTimer;
    private readonly DispatcherTimer sessionTimer;
    private readonly SpectrumAnalyzer analyzer = new(8192, 320);
    private readonly float[] analyzerBuffer = new float[8192];
    private HotKeys? hotKeys;
    private bool changeQueued;
    private DateTime lastTick = DateTime.UtcNow;
    private DateTime lastConfigWrite = DateTime.MinValue;
    private int viewers;
    private int mixerViewers;
    private string? bypassDeviceGuid;
    private long lastWritten;
    private int staleTicks;

    public AppModel(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        state = StateStore.Load();
        Prefs = new Prefs(state, ScheduleSave);
        Devices = new DeviceCenter(dispatcher);
        AutoEq = new AutoEqService(Path.Combine(AppPaths.Cache, "autoeq"));
        Squig = new SquigService(Path.Combine(AppPaths.Cache, "squig"));
        AutoEq.Changed += NotifyChanged;
        Squig.Changed += NotifyChanged;
        analyzer.TiltDbPerOctave = state.AnalyzerTilt;
        Meters.SpectrumFrequencies = analyzer.DisplayFrequencies.ToArray();

        saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => SaveNow(), dispatcher) { IsEnabled = false };
        configTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Normal, (_, _) => { configTimer!.Stop(); WriteConfig(); }, dispatcher) { IsEnabled = false };
        meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => MeterTick(), dispatcher) { IsEnabled = false };
        sessionTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => RefreshSessions(), dispatcher) { IsEnabled = false };

        Devices.Changed += () =>
        {
            RememberDevices();
            RefreshSessions();
            if (viewers > 0) Loopback.Follow(Devices.DefaultOutputDevice);
            ScheduleConfig();
            NotifyChanged();
        };
        Devices.VolumeChanged += NotifyChanged;
        RememberDevices();
        if (Snapshot.UsesDemoData) SetUpDemo();
        WriteConfig();
        if (!Snapshot.IsActive) UpdateShortcuts();
    }

    public event Action? Changed;
    /// ~30 times a second while a window shows meters.
    public event Action? Tick;

    public DeviceCenter Devices { get; }
    public SessionCenter Sessions { get; } = new();
    public LoopbackMonitor Loopback { get; } = new();
    public LiveMeters Meters { get; } = new();
    public AutoEqService AutoEq { get; }
    public SquigService Squig { get; }
    public Prefs Prefs { get; }

    public EngineStatus Status { get; private set; } = EngineStatus.Off;
    public string? LastError { get; private set; }
    public bool PowerOn => state.PowerOn;
    public CompareSlot CompareSlot { get; private set; } = CompareSlot.A;
    public bool CanUndo => undoStack.Count > 0;
    public bool CanRedo => redoStack.Count > 0;
    public IReadOnlyList<EqPreset> UserPresets => state.UserPresets;

    public Guid? SelectedBandId
    {
        get => selectedBandId;
        set
        {
            if (selectedBandId == value) return;
            selectedBandId = value;
            NotifyChanged();
        }
    }
    private Guid? selectedBandId;

    public void NotifyChanged()
    {
        // The web services report from thread-pool threads.
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(NotifyChanged);
            return;
        }
        if (changeQueued) return;
        changeQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            changeQueued = false;
            Changed?.Invoke();
        });
    }

    // MARK: - Power & Equalizer APO

    public AudioDevice? CurrentOutput => Devices.DefaultOutput;
    public string CurrentDeviceId => CurrentOutput?.Id ?? "none";
    public double SampleRate => CurrentOutput?.SampleRate ?? 48000;

    public void SetPower(bool on)
    {
        state.PowerOn = on;
        ScheduleSave();
        WriteConfig();
        NotifyChanged();
    }

    public void TogglePower() => SetPower(!PowerOn);

    /// Re-checks Equalizer APO (after the user installed it or ran its Configurator).
    public void Recheck()
    {
        Devices.Refresh();
        WriteConfig();
        NotifyChanged();
    }

    private void ScheduleConfig()
    {
        // At most ~25 writes a second while dragging; Equalizer APO reloads on every write.
        if (configTimer.IsEnabled) return;
        var since = DateTime.UtcNow - lastConfigWrite;
        configTimer.Interval = since > TimeSpan.FromMilliseconds(40) ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromMilliseconds(40) - since;
        configTimer.Start();
    }

    private void WriteConfig()
    {
        lastConfigWrite = DateTime.UtcNow;
        if (Snapshot.IsActive)
        {
            // Snapshots never touch Equalizer APO.
            Status = Snapshot.UsesDemoData ? EngineStatus.Running : EngineStatus.NeedsEqualizerApo;
            NotifyChanged();
            return;
        }
        if (!EqualizerApo.IsInstalled)
        {
            Status = EngineStatus.NeedsEqualizerApo;
            NotifyChanged();
            return;
        }
        if (!state.TookOverApoConfig)
        {
            if (EqualizerApo.HasForeignConfig)
            {
                Status = EngineStatus.NeedsTakeover;
                NotifyChanged();
                return;
            }
            state.TookOverApoConfig = true;
            ScheduleSave();
        }
        try
        {
            var devices = state.Profiles
                .Where(p => p.Key != "none")
                .Select(p =>
                {
                    var known = Devices.Outputs.FirstOrDefault(d => d.Id == p.Key);
                    var guid = known?.Guid ?? GuidOf(p.Key);
                    return new ApoConfig.Device(guid, known?.Name ?? state.DeviceNames.GetValueOrDefault(p.Key, guid),
                                                known?.Channels ?? state.DeviceChannels.GetValueOrDefault(p.Key, 2),
                                                known?.SampleRate ?? 48000, p.Value);
                })
                .Where(d => !string.Equals(d.Guid, bypassDeviceGuid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            EqualizerApo.Write(ApoConfig.Build(state.PowerOn, devices, state.LevelMatchedBypass));
            LastError = null;
            Status = ComputeStatus();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LastError = e.Message;
            Status = EngineStatus.WriteFailed;
        }
        NotifyChanged();
    }

    /// Lets Tapline replace Equalizer APO's configuration (a backup is kept). With `import`, the
    /// filters of the old configuration are first saved as a preset.
    public void TakeOverApoConfig(bool import)
    {
        if (import && EqualizerApo.ReadConfig() is string text)
        {
            try
            {
                var parsed = ParametricEqFormat.Parse(text);
                if (parsed.Bands.Count > 0)
                {
                    var preset = new EqPreset(L.T("Previous Equalizer APO configuration"), parsed.Bands.Take(DspSettings.MaxBands),
                                              parsed.PreampDb ?? 0, parsed.PreampDb == null) { Source = "Equalizer APO" };
                    state.UserPresets.Add(preset);
                }
            }
            catch (Exception e) when (e is ParametricEqParseException or FormatException or ArgumentException or InvalidOperationException)
            {
                // Nothing Tapline can read (only includes, graphic EQ, VST…): the backup still has it.
            }
        }
        state.TookOverApoConfig = true;
        ScheduleSave();
        WriteConfig();
    }

    /// Puts back the configuration Tapline replaced and stops managing Equalizer APO.
    public bool RestoreApoConfig()
    {
        state.PowerOn = false;
        state.TookOverApoConfig = false;
        ScheduleSave();
        var ok = false;
        try { ok = EqualizerApo.RestoreBackup(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastError = e.Message; }
        WriteConfig();
        return ok;
    }

    private EngineStatus ComputeStatus()
    {
        if (!state.PowerOn) return EngineStatus.Off;
        if (CurrentOutput is not AudioDevice output) return EngineStatus.NoOutput;
        if (!EqualizerApo.IsInstalledOn(output)) return EngineStatus.NotOnDevice;
        if (EqualizerApo.EnhancementsDisabled(output)) return EngineStatus.EnhancementsDisabled;
        return EngineStatus.Running;
    }

    private static string GuidOf(string deviceId) => deviceId.Contains("}.{") ? deviceId[(deviceId.LastIndexOf('.') + 1)..] : deviceId;

    private void RememberDevices()
    {
        foreach (var d in Devices.Outputs)
        {
            state.DeviceNames[d.Id] = d.Name;
            state.DeviceChannels[d.Id] = d.Channels;
        }
    }

    /// Leaves `device` unprocessed while a measurement runs, so it sees the speakers and the room.
    public async Task BypassWhile(AudioDevice device, Func<Task> action)
    {
        bypassDeviceGuid = device.Guid;
        WriteConfig();
        try
        {
            await Task.Delay(400);
            await action();
        }
        finally
        {
            bypassDeviceGuid = null;
            WriteConfig();
        }
    }

    // MARK: - Meters & analyzer

    /// Windows that draw meters or the spectrum call this while visible.
    public void MetersAppeared()
    {
        viewers++;
        if (!Snapshot.UsesDemoData) Loopback.Follow(Devices.DefaultOutputDevice);
        lastTick = DateTime.UtcNow;
        meterTimer.Start();
    }

    public void MetersDisappeared()
    {
        viewers = Math.Max(0, viewers - 1);
        if (viewers > 0) return;
        meterTimer.Stop();
        Loopback.Stop();
        Meters.Reset();
    }

    public void MixerAppeared()
    {
        mixerViewers++;
        RefreshSessions();
        sessionTimer.Start();
    }

    public void MixerDisappeared()
    {
        mixerViewers = Math.Max(0, mixerViewers - 1);
        if (mixerViewers == 0) sessionTimer.Stop();
    }

    public double AnalyzerTilt
    {
        get => state.AnalyzerTilt;
        set
        {
            state.AnalyzerTilt = value;
            analyzer.TiltDbPerOctave = value;
            ScheduleSave();
            NotifyChanged();
        }
    }

    private void MeterTick()
    {
        var now = DateTime.UtcNow;
        var dt = (now - lastTick).TotalSeconds;
        lastTick = now;
        if (Snapshot.UsesDemoData)
        {
            Tick?.Invoke();
            return;
        }
        var m = Loopback.TakeMeters();
        Meters.Ingest(m.PeakL, m.PeakR, m.RmsL, m.RmsR, m.Clipped, dt);
        // WASAPI loopback delivers nothing while nothing plays: show silence rather than the last sound.
        var total = Loopback.CopyRecent(analyzerBuffer);
        if (total == lastWritten)
        {
            if (++staleTicks > 3) Array.Clear(analyzerBuffer);
        }
        else
        {
            staleTicks = 0;
            lastWritten = total;
        }
        Meters.Spectrum = analyzer.Process(analyzerBuffer, Loopback.SampleRate);
        if (mixerViewers > 0) Sessions.UpdatePeaks();
        Tick?.Invoke();
    }

    private void RefreshSessions()
    {
        if (mixerViewers == 0 && Sessions.Apps.Count == 0) return;
        if (Sessions.Refresh(Devices.DefaultOutputDevice)) NotifyChanged();
    }

    /// A curve, meters and spectrum for screenshots (as on macOS).
    private void SetUpDemo()
    {
        var demo = DspSettings.Flat;
        demo.Bands =
        [
            new EqBand(FilterType.HighPass, 24, 0, 0.71),
            new EqBand(FilterType.LowShelf, 105, 4.2, 0.7),
            new EqBand(FilterType.Peak, 132, -3.5, 1.01),
            new EqBand(FilterType.Peak, 350, 2.1, 1.21),
            new EqBand(FilterType.Peak, 1875, 3.4, 1.8),
            new EqBand(FilterType.Peak, 5985, -4.8, 3.5),
            new EqBand(FilterType.HighShelf, 10_000, 2.5, 0.7),
        ];
        state.Profiles[CurrentDeviceId] = demo;
        selectedBandId = demo.Bands[4].Id;

        var frequencies = Meters.SpectrumFrequencies;
        var eq = ResponseCurve.ResponseDb(demo.ActiveBands, frequencies, 48000);
        var seed = 0x9E3779B97F4A7C15UL;
        double Noise()
        {
            seed = seed * 6364136223846793005UL + 1442695040888963407UL;
            return (seed >> 33) / (double)(1UL << 31) - 0.5;
        }
        var spectrum = new float[frequencies.Length];
        var smooth = 0.0;
        for (var i = 0; i < frequencies.Length; i++)
        {
            var f = frequencies[i];
            var octave = Math.Log2(f / 1000);
            var music = -34 - 1.2 * octave * octave + 7 * Math.Exp(-Math.Pow(Math.Log2(f / 70), 2)) + 4 * Math.Exp(-Math.Pow(Math.Log2(f / 2500), 2) * 2);
            var rolloff = f > 14_000 ? -(f - 14_000) / 400 : 0;
            smooth = 0.6 * smooth + 0.4 * Noise() * 9;
            spectrum[i] = (float)(music + rolloff + smooth + eq[i] + demo.EffectivePreampDb());
        }
        Meters.Spectrum = spectrum;
        Meters.Ingest(0.62f, 0.58f, 0.31f, 0.29f, 0, 0.03);
    }

    // MARK: - Shortcuts

    public bool GlobalShortcutsEnabled
    {
        get => state.GlobalShortcuts;
        set
        {
            state.GlobalShortcuts = value;
            ScheduleSave();
            UpdateShortcuts();
            NotifyChanged();
        }
    }

    /// Which shortcuts could not be registered (another app owns them).
    public IReadOnlyList<string> ShortcutConflicts { get; private set; } = [];

    private void UpdateShortcuts()
    {
        hotKeys?.UnregisterAll();
        ShortcutConflicts = [];
        if (!state.GlobalShortcuts) return;
        hotKeys ??= new HotKeys();
        var failed = new List<string>();
        if (!hotKeys.Register(1, System.Windows.Input.Key.P, TogglePower)) failed.Add("Ctrl+Alt+Shift+P");
        if (!hotKeys.Register(2, System.Windows.Input.Key.B, () => SetEqEnabled(!Settings.EqEnabled))) failed.Add("Ctrl+Alt+Shift+B");
        if (!hotKeys.Register(3, System.Windows.Input.Key.K, ToggleCompare)) failed.Add("Ctrl+Alt+Shift+K");
        ShortcutConflicts = failed;
    }

    public bool CloseToTray
    {
        get => state.CloseToTray;
        set
        {
            state.CloseToTray = value;
            ScheduleSave();
            NotifyChanged();
        }
    }

    /// Plays the bypassed sound at the loudness of the EQ, so that switching the EQ off
    /// compares the sound and not the level (a correction's preamp makes it quieter).
    public bool LevelMatchedBypass
    {
        get => state.LevelMatchedBypass;
        set
        {
            state.LevelMatchedBypass = value;
            ScheduleSave();
            ScheduleConfig();
            NotifyChanged();
        }
    }

    // MARK: - Device settings

    public DspSettings Settings => state.Profiles.TryGetValue(CurrentDeviceId, out var s) ? s : DspSettings.Flat;

    public DspSettings SettingsFor(string deviceId) => state.Profiles.TryGetValue(deviceId, out var s) ? s : DspSettings.Flat;

    public bool HasProfile(string deviceId) => state.Profiles.ContainsKey(deviceId);

    /// Snapshot for undo before a continuous edit (e.g. a drag) begins.
    public void Checkpoint()
    {
        undoStack.Add(Settings.Clone());
        if (undoStack.Count > 200) undoStack.RemoveAt(0);
        redoStack.Clear();
    }

    public void Mutate(Action<DspSettings> change, bool undoable = true)
    {
        var s = Settings.Clone();
        change(s);
        if (s.Equals(Settings)) return;
        if (undoable) Checkpoint();
        Store(s);
    }

    private void Store(DspSettings s, string? deviceId = null)
    {
        var id = deviceId ?? CurrentDeviceId;
        if (id == "none" && !Snapshot.UsesDemoData) return;
        state.Profiles[id] = s;
        ScheduleConfig();
        ScheduleSave();
        NotifyChanged();
    }

    public void Undo()
    {
        if (undoStack.Count == 0) return;
        var previous = undoStack[^1];
        undoStack.RemoveAt(undoStack.Count - 1);
        redoStack.Add(Settings.Clone());
        Store(previous);
    }

    public void Redo()
    {
        if (redoStack.Count == 0) return;
        var next = redoStack[^1];
        redoStack.RemoveAt(redoStack.Count - 1);
        undoStack.Add(Settings.Clone());
        Store(next);
    }

    public void SetEqEnabled(bool on) => Mutate(s => s.EqEnabled = on);

    public void ToggleCompare()
    {
        var id = CurrentDeviceId;
        var other = alternate.TryGetValue(id, out var a) ? a : Settings.Clone();
        alternate[id] = Settings.Clone();
        CompareSlot = CompareSlot == CompareSlot.A ? CompareSlot.B : CompareSlot.A;
        Store(other);
    }

    /// Copies the visible slot into the hidden one so A and B start identical.
    public void CopyToOtherSlot() => alternate[CurrentDeviceId] = Settings.Clone();

    // MARK: - Bands

    public EqBand? SelectedBand => SelectedBandId is Guid id && Settings.Bands.Any(b => b.Id == id) ? Settings.Bands.First(b => b.Id == id) : null;

    public Guid? AddBand(double frequency, double gain = 0, FilterType type = FilterType.Peak)
    {
        if (Settings.Bands.Count >= DspSettings.MaxBands) return null;
        var band = new EqBand(type, frequency, gain, type == FilterType.Peak ? 1.0 : 0.71).Clamped();
        Mutate(s => s.Bands.Add(band));
        SelectedBandId = band.Id;
        return band.Id;
    }

    public void RemoveBand(Guid id)
    {
        Mutate(s => s.Bands.RemoveAll(b => b.Id == id));
        if (SelectedBandId == id) SelectedBandId = Settings.Bands.Count > 0 ? Settings.Bands[^1].Id : null;
    }

    public void UpdateBand(Guid id, Func<EqBand, EqBand> change, bool undoable = true)
    {
        Mutate(s =>
        {
            var i = s.Bands.FindIndex(b => b.Id == id);
            if (i < 0) return;
            s.Bands[i] = change(s.Bands[i]).Clamped();
        }, undoable);
    }

    public void ResetBands()
    {
        Mutate(s =>
        {
            s.Bands = DspSettings.DefaultBands();
            s.Tone = new ToneSettings();
            s.PreampDb = 0;
            s.AutoPreamp = true;
        });
        SelectedBandId = null;
    }

    // MARK: - Presets

    public IEnumerable<EqPreset> AllPresets => BuiltInPresets.All.Concat(state.UserPresets);

    public EqPreset? MatchingPreset => AllPresets.FirstOrDefault(p => p.Matches(Settings));

    public EqPreset? MatchingPresetFor(DspSettings s) => AllPresets.FirstOrDefault(p => p.Matches(s));

    /// A preset's name for display: built-in names are translated.
    public static string DisplayName(EqPreset p) => p.IsBuiltIn ? L.T(p.Name) : p.Name;

    public void Apply(EqPreset preset)
    {
        Mutate(preset.ApplyTo);
        SelectedBandId = null;
    }

    public EqPreset SaveCurrentAsPreset(string name, string? source = null, bool includeEffects = false)
    {
        var preset = EqPreset.From(name, Settings, source, includeEffects);
        state.UserPresets.Add(preset);
        ScheduleSave();
        NotifyChanged();
        return preset;
    }

    public void RenamePreset(Guid id, string name)
    {
        var p = state.UserPresets.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        p.Name = name;
        ScheduleSave();
        NotifyChanged();
    }

    public void DeletePreset(Guid id)
    {
        state.UserPresets.RemoveAll(p => p.Id == id);
        ScheduleSave();
        NotifyChanged();
    }

    /// Imports an Equalizer APO / AutoEQ text, applies it, and saves it as a preset.
    public EqPreset? ImportParametricEq(string text, string name, string? source = null, bool saveAsPreset = true)
    {
        var parsed = ParametricEqFormat.Parse(text);
        Mutate(s =>
        {
            s.Bands = parsed.Bands.Take(DspSettings.MaxBands).ToList();
            s.Tone = new ToneSettings();
            s.EqEnabled = true;
            if (parsed.PreampDb is double pre)
            {
                s.AutoPreamp = false;
                s.PreampDb = pre.Clamp(DspSettings.PreampMin, DspSettings.PreampMax);
            }
            else
            {
                s.AutoPreamp = true;
            }
        });
        SelectedBandId = null;
        return saveAsPreset ? SaveCurrentAsPreset(name, source) : null;
    }

    /// Applies a generated correction (AutoEQ, squig.link fit, room) and saves it as a preset.
    public EqPreset ApplyCorrection(IEnumerable<EqBand> bands, string name, string? source, double? preampDb = null)
    {
        var list = bands.Take(DspSettings.MaxBands).ToList();
        Mutate(s =>
        {
            s.Bands = list;
            s.Tone = new ToneSettings();
            s.EqEnabled = true;
            if (preampDb is double pre)
            {
                s.AutoPreamp = false;
                s.PreampDb = pre.Clamp(DspSettings.PreampMin, DspSettings.PreampMax);
            }
            else
            {
                s.AutoPreamp = true;
            }
        });
        SelectedBandId = null;
        var existing = state.UserPresets.FindIndex(p => p.Name == name);
        if (existing >= 0)
        {
            var updated = EqPreset.From(name, Settings, source);
            updated.Id = state.UserPresets[existing].Id;
            state.UserPresets[existing] = updated;
            ScheduleSave();
            NotifyChanged();
            return updated;
        }
        return SaveCurrentAsPreset(name, source);
    }

    public string ExportParametricEq() =>
        ParametricEqFormat.Export(Settings.Bands, Settings.EffectivePreampDb(SampleRate));

    /// Imports an EQ file: Equalizer APO / AutoEQ text, or an RME ADI-2 preset (.adieqpr).
    public EqPreset? ImportEqFile(string text, string name)
    {
        if (!text.Contains("<ADI-2")) return ImportParametricEq(text, name);
        var parsed = Adi2Preset.Parse(text);
        if (parsed.Bands.Count == 0) throw new FormatException("No filters.");
        Mutate(s =>
        {
            s.Bands = parsed.Bands.Take(DspSettings.MaxBands).ToList();
            s.Tone = new ToneSettings();
            s.EqEnabled = true;
            s.AutoPreamp = true;
        });
        SelectedBandId = null;
        return SaveCurrentAsPreset(parsed.Name ?? name, "RME ADI-2");
    }

    /// The current EQ (tone controls included) as an RME ADI-2 preset file.
    /// Left and right get their own bands (Dual EQ) when the EQ has channel-specific bands.
    public string ExportAdi2Preset(string name)
    {
        var s = Settings;
        var bands = s.Bands.Where(b => b.IsEnabled).Concat(s.Tone.Bands.Where(b => b.IsEnabled)).ToList();
        if (!s.HasChannelSpecificBands) return Adi2Preset.Export(bands, name);
        return Adi2Preset.Export(bands.Where(b => b.Channels.Includes(EqChannels.Left)).ToList(), name,
                                 bands.Where(b => b.Channels.Includes(EqChannels.Right)).ToList());
    }

    public void ResetEverything()
    {
        state.Profiles.Clear();
        state.UserPresets.Clear();
        StateStore.ForgetUnreadable();
        alternate.Clear();
        undoStack.Clear();
        redoStack.Clear();
        SelectedBandId = null;
        SaveNow();
        WriteConfig();
        NotifyChanged();
    }

    // MARK: - Persistence

    private void ScheduleSave()
    {
        if (!saveTimer.IsEnabled) saveTimer.Start();
    }

    public void SaveNow()
    {
        saveTimer.Stop();
        try { StateStore.Save(state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastError = e.Message; }
    }

    public void Shutdown()
    {
        // Quitting during a measurement: give the device its EQ back.
        if (bypassDeviceGuid != null)
        {
            bypassDeviceGuid = null;
            WriteConfig();
        }
        SaveNow();
        meterTimer.Stop();
        sessionTimer.Stop();
        Loopback.Dispose();
        hotKeys?.Dispose();
        Devices.Dispose();
    }
}
