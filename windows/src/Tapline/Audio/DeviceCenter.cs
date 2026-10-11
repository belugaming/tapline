using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Tapline.UI;

namespace Tapline.Audio;

/// Endpoint form factors (PKEY_AudioEndpoint_FormFactor).
enum FormFactor
{
    RemoteNetwork = 0, Speakers = 1, LineLevel = 2, Headphones = 3, Microphone = 4, Headset = 5,
    Handset = 6, DigitalPassthrough = 7, Spdif = 8, Hdmi = 9, Unknown = 10,
}

/// An audio endpoint as the UI sees it.
sealed record AudioDevice(string Id, string Name, string Adapter, bool IsOutput, int Channels, int SampleRate, FormFactor FormFactor)
{
    /// The endpoint GUID, e.g. "{6b8f…}": what Equalizer APO's Device: command and registry keys use.
    public string Guid => Id.Contains("}.{") ? Id[(Id.LastIndexOf('.') + 1)..] : Id;

    public string Glyph => FormFactor switch
    {
        FormFactor.Headphones or FormFactor.Headset or FormFactor.Handset => Glyphs.Headphones,
        FormFactor.Microphone => Glyphs.Microphone,
        FormFactor.Hdmi => Glyphs.Monitor,
        _ => IsOutput ? Glyphs.Speakers : Glyphs.Microphone,
    };

    public bool IsHeadphones => FormFactor is FormFactor.Headphones or FormFactor.Headset;
}

/// Lists the audio endpoints, follows the default output and input, and controls the output volume.
/// Raises `Changed` on the UI thread.
sealed class DeviceCenter : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer debounce;
    private MMDevice? outputDevice;
    private AudioEndpointVolume? outputVolume;

    public DeviceCenter(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { debounceTick(); }, dispatcher) { IsEnabled = false };
        Refresh();
        try { enumerator.RegisterEndpointNotificationCallback(this); } catch { /* Notifications are a nicety. */ }
    }

    public event Action? Changed;
    /// Output volume or mute changed (from Tapline or elsewhere).
    public event Action? VolumeChanged;

    public IReadOnlyList<AudioDevice> Outputs { get; private set; } = [];
    public IReadOnlyList<AudioDevice> Inputs { get; private set; } = [];
    public AudioDevice? DefaultOutput { get; private set; }
    public AudioDevice? DefaultInput { get; private set; }
    public MMDevice? DefaultOutputDevice => outputDevice;

    public float OutputVolume => Safe(() => outputVolume?.MasterVolumeLevelScalar ?? 0, 0f);
    public bool OutputMuted => Safe(() => outputVolume?.Mute ?? false, false);
    public bool HasOutputVolume => outputVolume != null;

    private void debounceTick()
    {
        debounce.Stop();
        Refresh();
    }

    public void Refresh()
    {
        Outputs = List(DataFlow.Render);
        Inputs = List(DataFlow.Capture);
        DefaultInput = Default(DataFlow.Capture, Inputs);
        var output = Default(DataFlow.Render, Outputs);
        if (output?.Id != DefaultOutput?.Id || outputDevice == null)
        {
            DefaultOutput = output;
            AttachOutput(output);
        }
        else
        {
            DefaultOutput = output;
        }
        Changed?.Invoke();
    }

    private void AttachOutput(AudioDevice? device)
    {
        if (outputVolume != null)
        {
            try { outputVolume.OnVolumeNotification -= OnVolume; } catch { }
        }
        outputVolume = null;
        outputDevice?.Dispose();
        outputDevice = null;
        if (device == null) return;
        try
        {
            outputDevice = enumerator.GetDevice(device.Id);
            outputVolume = outputDevice.AudioEndpointVolume;
            outputVolume.OnVolumeNotification += OnVolume;
        }
        catch
        {
            outputVolume = null;
        }
    }

    private void OnVolume(AudioVolumeNotificationData data) =>
        dispatcher.BeginInvoke(() => VolumeChanged?.Invoke());

    private IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        var result = new List<AudioDevice>();
        try
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (d)
                {
                    var device = Describe(d, flow == DataFlow.Render);
                    if (device != null) result.Add(device);
                }
            }
        }
        catch
        {
            // Audio service restarting: keep an empty list until the next notification.
        }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static AudioDevice? Describe(MMDevice d, bool output)
    {
        try
        {
            var name = Safe(() => d.FriendlyName, "?");
            var adapter = Safe(() => d.DeviceFriendlyName, "");
            var formFactor = Safe(() =>
            {
                var props = d.Properties;
                return props.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor)
                    ? (FormFactor)Convert.ToInt32(props[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value)
                    : FormFactor.Unknown;
            }, FormFactor.Unknown);
            var (channels, rate) = Safe(() =>
            {
                var format = d.AudioClient.MixFormat;
                return (format.Channels, format.SampleRate);
            }, (2, 48000));
            return new AudioDevice(d.ID, name, adapter, output, channels, rate, formFactor);
        }
        catch
        {
            return null;
        }
    }

    private AudioDevice? Default(DataFlow flow, IReadOnlyList<AudioDevice> list)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)) return null;
            using var d = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return list.FirstOrDefault(x => x.Id == d.ID) ?? Describe(d, flow == DataFlow.Render);
        }
        catch
        {
            return null;
        }
    }

    public MMDevice? Open(AudioDevice device)
    {
        try { return enumerator.GetDevice(device.Id); }
        catch { return null; }
    }

    public bool SetDefault(AudioDevice device)
    {
        var ok = PolicyConfig.SetDefault(device.Id);
        Refresh();
        return ok;
    }

    public void SetOutputVolume(float v)
    {
        if (outputVolume == null) return;
        try { outputVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1); } catch { }
    }

    public void SetOutputMuted(bool muted)
    {
        if (outputVolume == null) return;
        try { outputVolume.Mute = muted; } catch { }
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }

    // IMMNotificationClient: called on a COM thread.
    private void Schedule() => dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Schedule();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Schedule();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Schedule();
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Schedule();
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        AttachOutput(null);
        enumerator.Dispose();
    }
}
