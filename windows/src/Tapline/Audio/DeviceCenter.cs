using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Tapline.Core;
using Tapline.UI;

namespace Tapline.Audio;

/// Endpoint form factors (PKEY_AudioEndpoint_FormFactor).
enum FormFactor
{
    RemoteNetwork = 0, Speakers = 1, LineLevel = 2, Headphones = 3, Microphone = 4, Headset = 5,
    Handset = 6, DigitalPassthrough = 7, Spdif = 8, Hdmi = 9, Unknown = 10,
}

/// An audio endpoint as the UI sees it. `Layout`: an output's speakers, one position per channel
/// of its mix format (from the format's speaker mask, see `DeviceCenter.LayoutOf`); null for inputs.
sealed record AudioDevice(string Id, string Name, string Adapter, bool IsOutput, int Channels, int SampleRate, FormFactor FormFactor,
                          SpeakerLayout? Layout = null)
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

    /// A multichannel speaker output (5.1 … 7.1.4) that Tapline processes speaker by speaker.
    public bool IsSurround => IsOutput && ApoConfig.ProcessesPerSpeaker(Channels, Layout);

    /// Software devices (virtual cables, mixers, other apps' correction drivers). Windows lists them
    /// like hardware, so they are recognised by name.
    public bool IsVirtual => VirtualNames.Any(n => Name.Contains(n, StringComparison.OrdinalIgnoreCase)
                                                  || Adapter.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] VirtualNames =
        ["Virtual", "VB-Audio", "Voicemeeter", "SoundID", "Sonarworks", "Steam Streaming", "NVIDIA Broadcast", "Krisp"];
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
            var (channels, rate, mask) = Safe(() =>
            {
                var format = d.AudioClient.MixFormat;
                return (format.Channels, format.SampleRate, ChannelMask(format));
            }, (2, 48000, 0u));
            var layout = output ? LayoutOf(d, channels, mask) : null;
            return new AudioDevice(d.ID, name, adapter, output, channels, rate, formFactor, layout);
        }
        catch
        {
            return null;
        }
    }

    /// PKEY_AudioEndpoint_PhysicalSpeakers: the speaker mask set in the Sound control panel (VT_UI4).
    private static readonly PropertyKey PhysicalSpeakers = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 3);

    /// The speaker mask of an extensible mix format (WAVEFORMATEXTENSIBLE.dwChannelMask, at byte 20), 0 for any other.
    private static uint ChannelMask(WaveFormat format)
    {
        if (format is not WaveFormatExtensible || format.ExtraSize < 22) return 0;
        var pointer = WaveFormat.MarshalToPtr(format);
        try
        {
            return Marshal.SizeOf(format) >= 24 ? unchecked((uint)Marshal.ReadInt32(pointer, 20)) : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// The speakers of an output: the mix format's speaker mask (what Equalizer APO's channels follow),
    /// else the speaker setup when it names as many channels, else Windows' default for the channel count.
    private static SpeakerLayout LayoutOf(MMDevice d, int channels, uint mask)
    {
        if ((mask & SpeakerLayout.SpeakerBits) == 0)
        {
            var physical = Safe(() =>
            {
                var props = d.Properties;
                return props.Contains(PhysicalSpeakers) ? Convert.ToUInt32(props[PhysicalSpeakers].Value) : 0u;
            }, 0u);
            if (System.Numerics.BitOperations.PopCount(physical & SpeakerLayout.SpeakerBits) == channels) mask = physical;
        }
        return SpeakerLayout.FromChannelMask(mask, channels);
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
