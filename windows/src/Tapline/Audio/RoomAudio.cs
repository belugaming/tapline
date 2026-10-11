using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Tapline.Core;
using Tapline.Infrastructure;
using Tapline.Model;
using Tapline.UI;

namespace Tapline.Audio;

/// Which speakers a room measurement plays on.
enum RoomSpeakers
{
    /// Each speaker on its own in one run, plus timing chirps.
    Stereo,
    Both,
    Left,
    Right,
}

enum RoomPhase { Idle, CheckingLevel, Measuring, Analyzing }

/// A failure with a message for the user.
sealed class RoomAudioException(string message, bool microphoneBlocked = false) : Exception(message)
{
    /// Windows (or a muted / disconnected microphone) gave Tapline no microphone signal.
    public bool MicrophoneBlocked { get; } = microphoneBlocked;
}

/// Measures speakers in the room: plays a sine sweep (or pink noise for a level
/// check) on the output device while recording a microphone, then turns the
/// recording into a frequency response.
///
/// Equalizer APO processes every app, Tapline's own sweep included, so each run
/// takes the output device out of Equalizer APO's configuration while it plays
/// (`AppModel.BypassWhile`): the measurement sees the speakers and the room without Tapline's EQ.
///
/// Lives on the UI thread; `Changed` fires there.
sealed class RoomAudio(AppModel model)
{
    public sealed class Measurement
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DateTime Date { get; } = DateTime.Now;
        public required RoomSpeakers Speakers { get; init; }
        /// Both speakers (for a stereo run: the power average of the two).
        public required FrequencyResponse Response { get; init; }
        public FrequencyResponse? Left { get; init; }
        public FrequencyResponse? Right { get; init; }
        /// Right arrival minus left arrival, seconds (stereo runs).
        public double? ArrivalDifference { get; init; }
        public bool? OppositePolarity { get; init; }
        public required double PeakDb { get; init; }
        public required double SnrDb { get; init; }

        public FrequencyResponse? LeftResponse => Left ?? (Speakers == RoomSpeakers.Left ? Response : null);
        public FrequencyResponse? RightResponse => Right ?? (Speakers == RoomSpeakers.Right ? Response : null);
    }

    public const double SweepSeconds = 8.0;
    private const double PreRoll = 0.6;
    private const double Tail = 1.5;
    private const string MicrophoneSettingsUri = "ms-settings:privacy-microphone";

    private readonly List<Measurement> measurements = [];
    private double runPeakDb = -120;

    public event Action? Changed;

    public RoomPhase Phase { get; private set; } = RoomPhase.Idle;
    /// 0…1 while measuring.
    public double Progress { get; private set; }
    public IReadOnlyList<Measurement> Measurements => measurements;
    /// Live microphone level during a level check or measurement, dBFS.
    public double InputPeakDb { get; private set; } = -120;
    public double InputRmsDb { get; private set; } = -120;
    public FrequencyResponse? Calibration { get; private set; }
    public string? CalibrationName { get; private set; }
    public string? LastMessage { get; set; }
    /// The last run got no microphone signal, probably because of Windows' privacy settings.
    public bool MicrophoneBlocked { get; set; }

    public bool IsBusy => Phase != RoomPhase.Idle;

    public FrequencyResponse? AveragedResponse { get; private set; }
    public FrequencyResponse? AveragedLeft { get; private set; }
    public FrequencyResponse? AveragedRight { get; private set; }

    /// Mean over the stereo runs (the positions surround the listening spot).
    public double? ArrivalDifference
    {
        get
        {
            var values = measurements.Where(m => m.ArrivalDifference.HasValue).Select(m => m.ArrivalDifference!.Value).ToList();
            return values.Count == 0 ? null : values.Average();
        }
    }

    /// True if any run heard opposite polarity, false if runs were clear, null if none could tell.
    public bool? OppositePolarity
    {
        get
        {
            var values = measurements.Where(m => m.OppositePolarity.HasValue).Select(m => m.OppositePolarity!.Value).ToList();
            return values.Count == 0 ? null : values.Contains(true);
        }
    }

    public double WorstSnrDb => measurements.Count == 0 ? 0 : measurements.Min(m => m.SnrDb);

    public static string Title(RoomSpeakers s) => L.T(s switch
    {
        RoomSpeakers.Stereo => "Left & right",
        RoomSpeakers.Both => "Together",
        RoomSpeakers.Left => "Left",
        _ => "Right",
    });

    /// The raw values macOS stores for `RoomMeasurementService.Speakers`.
    public static string Key(RoomSpeakers s) => s switch
    {
        RoomSpeakers.Stereo => "stereo",
        RoomSpeakers.Both => "both",
        RoomSpeakers.Left => "left",
        _ => "right",
    };

    public static RoomSpeakers ParseSpeakers(string? key) => key switch
    {
        "both" => RoomSpeakers.Both,
        "left" => RoomSpeakers.Left,
        "right" => RoomSpeakers.Right,
        _ => RoomSpeakers.Stereo,
    };

    /// Windows' microphone privacy page.
    public static void OpenMicrophoneSettings() => Ui.OpenUrl(MicrophoneSettingsUri);

    // MARK: - Microphone

    public bool LoadCalibration(string path)
    {
        if (Dialogs.ReadText(path) is not { } text || FrequencyResponse.Parse(text) is not { } fr) return false;
        Calibration = fr;
        CalibrationName = System.IO.Path.GetFileName(path);
        Changed?.Invoke();
        return true;
    }

    public void ClearCalibration()
    {
        Calibration = null;
        CalibrationName = null;
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        measurements.RemoveAll(m => m.Id == id);
        MeasurementsChanged();
    }

    public void ClearMeasurements()
    {
        measurements.Clear();
        MeasurementsChanged();
    }

    private void Add(Measurement m)
    {
        measurements.Add(m);
        MeasurementsChanged();
    }

    private void MeasurementsChanged()
    {
        AveragedResponse = RoomResponse.Average(measurements.Select(m => m.Response).ToList());
        AveragedLeft = RoomResponse.Average(measurements.Select(m => m.LeftResponse).OfType<FrequencyResponse>().ToList());
        AveragedRight = RoomResponse.Average(measurements.Select(m => m.RightResponse).OfType<FrequencyResponse>().ToList());
        Changed?.Invoke();
    }

    // MARK: - Level check

    /// Plays three seconds of pink noise at the sweep's level and shows what the microphone hears.
    public async Task CheckLevel(AudioDevice output, AudioDevice input, int channel, RoomSpeakers speakers)
    {
        if (IsBusy) return;
        MicrophoneBlocked = false;
        Phase = RoomPhase.CheckingLevel;
        Changed?.Invoke();
        try
        {
            const double seconds = 3.0;
            await Record(output, input, channel, 0.3, (rate, _) =>
            {
                var noise = PinkNoise((int)(seconds * rate), 0.12f);
                var silent = new float[noise.Length];
                return (speakers == RoomSpeakers.Right ? silent : noise, speakers == RoomSpeakers.Left ? silent : noise);
            });
            LastMessage = LevelAdvice();
        }
        catch (Exception e)
        {
            Report(e);
        }
        finally
        {
            Phase = RoomPhase.Idle;
            Changed?.Invoke();
        }
    }

    /// Judged on the loudest moment of the check (the noise ends before the recording does).
    private string LevelAdvice()
    {
        if (runPeakDb > -3) return L.T("Too loud: the microphone is close to clipping. Turn the volume or the preamp gain down.");
        if (runPeakDb < -45) return L.T("Too quiet. Turn the volume up, raise the preamp gain, or check the microphone channel and phantom power.");
        return L.T("Level looks good.");
    }

    // MARK: - Measurement

    public async Task Measure(AudioDevice output, AudioDevice input, int channel, RoomSpeakers speakers)
    {
        if (IsBusy) return;
        LastMessage = null;
        MicrophoneBlocked = false;
        Phase = RoomPhase.Measuring;
        Progress = 0;
        Changed?.Invoke();
        try
        {
            if (speakers == RoomSpeakers.Stereo) await MeasureStereo(output, input, channel);
            else await MeasureSingle(output, input, channel, speakers);
        }
        catch (Exception e)
        {
            Report(e);
        }
        finally
        {
            Phase = RoomPhase.Idle;
            Progress = 0;
            Changed?.Invoke();
        }
    }

    private async Task MeasureSingle(AudioDevice output, AudioDevice input, int channel, RoomSpeakers speakers)
    {
        var (recording, inputRate, outputRate) = await Record(output, input, channel, Tail, (rate, micRate) =>
        {
            var sweep = new SineSweep(rate, SweepSeconds, endFrequency: SineSweep.EndFrequencyFor(rate, micRate)).Signal;
            var preRoll = (int)(PreRoll * rate);
            var padded = new float[preRoll + sweep.Length];
            sweep.CopyTo(padded, preRoll);
            var silent = new float[padded.Length];
            return (speakers == RoomSpeakers.Right ? silent : padded, speakers == RoomSpeakers.Left ? silent : padded);
        });

        Phase = RoomPhase.Analyzing;
        Changed?.Invoke();
        var calibration = Calibration;
        var (response, peakDb, snrDb) = await Task.Run(() =>
        {
            // The same sweep range as the one played, at the microphone's rate.
            var reference = new SineSweep(inputRate, SweepSeconds, endFrequency: SineSweep.EndFrequencyFor(outputRate, inputRate));
            var ir = reference.ImpulseResponse(recording);
            var fr = RoomResponse.FromImpulse(ir, inputRate);
            if (calibration != null && fr != null) fr = RoomResponse.ApplyCalibration(fr, calibration);
            float peak = 0;
            foreach (var v in recording) peak = Math.Max(peak, Math.Abs(v));
            var noise = Rms(recording, 0, Math.Max(1, (int)(PreRoll * 0.8 * inputRate)));
            var sweepPart = Rms(recording, (int)(PreRoll * inputRate), (int)(SweepSeconds * inputRate));
            var snr = 20 * Math.Log10(Math.Max(sweepPart, 1e-9) / Math.Max(noise, 1e-9));
            return (fr, 20 * Math.Log10(Math.Max(peak, 1e-9)), snr);
        });

        if (response == null)
        {
            LastMessage = L.T("The measurement failed. Check that the microphone hears the speakers.");
            return;
        }
        Add(new Measurement { Speakers = speakers, Response = response, PeakDb = peakDb, SnrDb = snrDb });
        LastMessage = ResultMessage(peakDb, snrDb);
    }

    /// One recording: a sweep per speaker, then left / right / left timing chirps.
    private async Task MeasureStereo(AudioDevice output, AudioDevice input, int channel)
    {
        const double sweepSeconds = SweepSeconds;
        var (recording, inputRate, outputRate) = await Record(output, input, channel, 0.4,
            (rate, micRate) => new StereoSweepPlan(rate, sweepSeconds, SineSweep.EndFrequencyFor(rate, micRate)).Render());

        Phase = RoomPhase.Analyzing;
        Changed?.Invoke();
        var calibration = Calibration;
        var analysis = await Task.Run(() => StereoSweepPlan.Analyze(recording, inputRate, outputRate, sweepSeconds));
        if (analysis == null)
        {
            LastMessage = L.T("The measurement failed. Check that the microphone hears the speakers.");
            return;
        }
        var left = analysis.Left;
        var right = analysis.Right;
        if (calibration != null)
        {
            left = RoomResponse.ApplyCalibration(left, calibration);
            right = RoomResponse.ApplyCalibration(right, calibration);
        }
        if (RoomResponse.Average([left, right]) is not { } both) return;
        var snr = Math.Min(analysis.SnrLeftDb, analysis.SnrRightDb);
        Add(new Measurement
        {
            Speakers = RoomSpeakers.Stereo,
            Response = both,
            Left = left,
            Right = right,
            ArrivalDifference = analysis.ArrivalDifference,
            OppositePolarity = analysis.OppositePolarity,
            PeakDb = analysis.PeakDb,
            SnrDb = snr,
        });
        LastMessage = ResultMessage(analysis.PeakDb, snr);
    }

    private static string ResultMessage(double peakDb, double snrDb)
    {
        if (peakDb > -1) return L.T("The microphone clipped during the sweep. Lower the volume and measure again.");
        if (snrDb < 20) return L.T("The room is noisy or the level is low. The result may be unreliable.");
        return L.T("Measured. Move the microphone a little and measure again to average several positions.");
    }

    private void Report(Exception e)
    {
        if (e is RoomAudioException r)
        {
            LastMessage = r.Message;
            MicrophoneBlocked = r.MicrophoneBlocked;
        }
        else
        {
            Log(e);
            LastMessage = e.Message;
        }
    }

    /// Device errors go to errors.log, so a tester can tell what Windows said.
    private static void Log(Exception e) => CrashLog.Write(e);

    private static double Rms(float[] x, int start, int count)
    {
        var a = Math.Clamp(start, 0, x.Length);
        var b = Math.Clamp(start + Math.Max(count, 0), a, x.Length);
        double sum = 0;
        for (var i = a; i < b; i++) sum += (double)x[i] * x[i];
        return Math.Sqrt(sum / Math.Max(b - a, 1));
    }

    // MARK: - Playback + recording

    /// Runs one playback + recording with the output taken out of Equalizer APO.
    private async Task<(float[] Recording, double InputRate, double OutputRate)> Record(AudioDevice output, AudioDevice input, int channel, double extraSeconds,
                                                                                        Func<double, double, (float[] Left, float[] Right)> signals)
    {
        (float[] Recording, double InputRate, double OutputRate) result = (Array.Empty<float>(), 0, 0);
        await model.BypassWhile(output, async () => result = await Run(output, input, channel, extraSeconds, signals));
        return result;
    }

    /// Records one channel of the input as float samples and keeps the live level.
    private sealed class Recorder(int capacity)
    {
        private readonly object gate = new();
        private float[] samples = new float[Math.Max(capacity, 4096)];
        private int length;
        private float peak;
        private float maxPeak;
        private double sumSquares;
        private int count;

        public void Append(byte[] buffer, int bytes, int channels, int bytesPerSample, bool isFloat, int channel)
        {
            if (bytesPerSample <= 0 || channels <= 0) return;
            var frameBytes = bytesPerSample * channels;
            var frames = bytes / frameBytes;
            if (frames <= 0) return;
            lock (gate)
            {
                if (length + frames > samples.Length) Array.Resize(ref samples, Math.Max(samples.Length * 2, length + frames));
                for (var i = 0; i < frames; i++)
                {
                    var v = LoopbackMonitor.Read(buffer, i * frameBytes + channel * bytesPerSample, bytesPerSample, isFloat);
                    if (!float.IsFinite(v)) v = 0;
                    samples[length++] = v;
                    var a = Math.Abs(v);
                    if (a > peak) peak = a;
                    if (a > maxPeak) maxPeak = a;
                    sumSquares += (double)v * v;
                }
                count += frames;
            }
        }

        /// Peak and RMS since the last call.
        public (float Peak, double Rms) TakeLevels()
        {
            lock (gate)
            {
                var result = (peak, count > 0 ? Math.Sqrt(sumSquares / count) : 0);
                peak = 0;
                sumSquares = 0;
                count = 0;
                return result;
            }
        }

        /// The loudest sample so far.
        public float MaxPeak
        {
            get { lock (gate) return maxPeak; }
        }

        public float[] All()
        {
            lock (gate) return samples.AsSpan(0, length).ToArray();
        }
    }

    /// Plays the left / right signals in channels 1 and 2 of the device's mix format (32-bit float),
    /// silence in the other channels and after the end. Read on WasapiOut's playback thread.
    private sealed class SignalProvider(float[] left, float[] right, int count, WaveFormat format) : IWaveProvider
    {
        private long position;

        public WaveFormat WaveFormat => format;

        public int Read(byte[] buffer, int offset, int bytes)
        {
            var target = buffer.AsSpan(offset, bytes);
            target.Clear();
            var channels = Math.Max(1, format.Channels);
            var frames = bytes / (4 * channels);
            var samples = MemoryMarshal.Cast<byte, float>(target[..(frames * 4 * channels)]);
            for (var i = 0; i < frames; i++)
            {
                var p = position + i;
                if (p >= count) break;
                float l = left[p], r = right[p];
                if (channels == 1)
                {
                    samples[i] = l != 0 ? l : r;
                }
                else
                {
                    samples[i * channels] = l;
                    samples[i * channels + 1] = r;
                }
            }
            position += frames;
            // Keep returning silence: playback runs until it is stopped.
            return bytes;
        }
    }

    /// Set from the audio threads when a device fails mid-run.
    private sealed class FailureBox
    {
        private Exception? value;
        public Exception? Value => Volatile.Read(ref value);
        public void Set(Exception e) => Interlocked.CompareExchange(ref value, e, null);
    }

    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int AudclntDeviceInUse = unchecked((int)0x8889000A);

    /// Plays the left / right signals made by `signals` (given the output's mix-format sample rate and
    /// the input's rate) on `output` and records one channel of `input` for the signal's length plus
    /// `extraSeconds`. Returns the recording, the input rate and the output rate.
    private async Task<(float[] Recording, double InputRate, double OutputRate)> Run(AudioDevice output, AudioDevice input, int channel, double extraSeconds,
                                                                                     Func<double, double, (float[] Left, float[] Right)> signals)
    {
        MMDevice? outDevice = null, inDevice = null;
        WasapiOut? player = null;
        WasapiCapture? capture = null;
        runPeakDb = -120;
        try
        {
            outDevice = model.Devices.Open(output) ?? throw new RoomAudioException(L.F("Could not open the audio devices: %@", output.Name));
            inDevice = model.Devices.Open(input) ?? throw new RoomAudioException(L.F("Could not open the audio devices: %@", input.Name));

            // Output: the shared-mode mix format, so Windows does not have to convert anything.
            WaveFormat outFormat;
            try
            {
                using var probe = outDevice.AudioClient;
                var mix = probe.MixFormat;
                var rate = mix.SampleRate > 0 ? mix.SampleRate : output.SampleRate;
                var channels = mix.Channels > 0 ? mix.Channels : Math.Max(output.Channels, 2);
                outFormat = LoopbackMonitor.IsFloat(mix) && mix.BitsPerSample == 32 && mix.SampleRate > 0 && mix.Channels > 0
                    ? mix
                    : new WaveFormatExtensible(rate, 32, channels);
                player = new WasapiOut(outDevice, AudioClientShareMode.Shared, true, 100);
            }
            catch (Exception e)
            {
                throw Wrap(e, "Could not open the audio devices: %@", output, isInput: false);
            }

            // Input: shared mode, the device's own format; one channel is kept.
            WaveFormat inFormat;
            try
            {
                capture = new WasapiCapture(inDevice, false, 100);
                inFormat = capture.WaveFormat;
            }
            catch (Exception e)
            {
                throw Wrap(e, "Could not open the audio devices: %@", input, isInput: true);
            }
            if (inFormat.Channels <= 0 || inFormat.SampleRate <= 0)
                throw new RoomAudioException(L.T("The selected input has no channels."));
            var ch = Math.Clamp(channel, 0, inFormat.Channels - 1);

            // The signals take a moment to compute (the sweep normalises itself with a long FFT).
            float[] left, right;
            try
            {
                (left, right) = await Task.Run(() => signals(outFormat.SampleRate, inFormat.SampleRate));
            }
            catch (Exception e)
            {
                Log(e);
                throw new RoomAudioException(L.T("Could not prepare the test signal."));
            }
            var count = Math.Min(left.Length, right.Length);
            if (count == 0) throw new RoomAudioException(L.T("Could not prepare the test signal."));
            var seconds = count / (double)outFormat.SampleRate + extraSeconds;

            var store = new Recorder((int)((seconds + 1) * inFormat.SampleRate));
            var inputFailure = new FailureBox();
            var outputFailure = new FailureBox();
            var inChannels = inFormat.Channels;
            var bytesPerSample = inFormat.BitsPerSample / 8;
            var isFloat = LoopbackMonitor.IsFloat(inFormat);
            capture.DataAvailable += (_, e) => store.Append(e.Buffer, e.BytesRecorded, inChannels, bytesPerSample, isFloat, ch);
            capture.RecordingStopped += (_, e) => { if (e.Exception != null) inputFailure.Set(e.Exception); };
            player.PlaybackStopped += (_, e) => { if (e.Exception != null) outputFailure.Set(e.Exception); };

            try
            {
                player.Init(new SignalProvider(left, right, count, outFormat));
            }
            catch (Exception e)
            {
                throw Wrap(e, "Could not start the audio devices: %@", output, isInput: false);
            }
            // Recording first, so it catches the start of the signal.
            try
            {
                capture.StartRecording();
            }
            catch (Exception e)
            {
                throw Wrap(e, "Could not start the audio devices: %@", input, isInput: true);
            }
            try
            {
                player.Play();
            }
            catch (Exception e)
            {
                throw Wrap(e, "Could not start the audio devices: %@", output, isInput: false);
            }

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(100);
                if (inputFailure.Value is { } fi) throw Wrap(fi, "Could not start the audio devices: %@", input, isInput: true);
                if (outputFailure.Value is { } fo) throw Wrap(fo, "Could not start the audio devices: %@", output, isInput: false);
                var (peak, rms) = store.TakeLevels();
                InputPeakDb = 20 * Math.Log10(Math.Max(peak, 1e-9));
                InputRmsDb = 20 * Math.Log10(Math.Max(rms, 1e-9));
                runPeakDb = Math.Max(runPeakDb, InputPeakDb);
                if (Phase == RoomPhase.Measuring) Progress = Math.Min(1, clock.Elapsed.TotalSeconds / seconds);
                Changed?.Invoke();
            }

            // Real microphones always hiss a little: digital silence means Windows withheld the
            // microphone (privacy settings) or the device is muted.
            if (store.MaxPeak == 0)
            {
                throw new RoomAudioException(MicrophoneDeniedBySettings() ? MicrophoneAccessMessage : L.T(
                    "The microphone sent only silence. Check that it is connected and not muted, and that Windows lets desktop apps use it (Settings › Privacy & security › Microphone)."),
                    microphoneBlocked: true);
            }
            return (store.All(), inFormat.SampleRate, outFormat.SampleRate);
        }
        finally
        {
            await Stop(player, capture, outDevice, inDevice);
        }
    }

    /// Stops playback, then recording, and releases the devices, off the UI thread (stopping
    /// joins the audio threads). Never throws; gives up waiting after a few seconds.
    private static async Task Stop(WasapiOut? player, WasapiCapture? capture, MMDevice? outDevice, MMDevice? inDevice)
    {
        if (player == null && capture == null && outDevice == null && inDevice == null) return;
        var work = Task.Run(() =>
        {
            try { player?.Stop(); } catch { }
            try { capture?.StopRecording(); } catch { }
            try { player?.Dispose(); } catch { }
            try { capture?.Dispose(); } catch { }
            try { outDevice?.Dispose(); } catch { }
            try { inDevice?.Dispose(); } catch { }
        });
        try { await Task.WhenAny(work, Task.Delay(3000)); } catch { }
    }

    private static string MicrophoneAccessMessage =>
        L.T("Tapline needs microphone access to measure your speakers.") + " "
        + L.T("Turn on “Let desktop apps access your microphone” in Settings › Privacy & security › Microphone, then try again.");

    private static RoomAudioException Wrap(Exception e, string format, AudioDevice? device, bool isInput)
    {
        if (e is RoomAudioException r) return r;
        Log(e);
        if (isInput && IsAccessDenied(e)) return new RoomAudioException(MicrophoneAccessMessage, microphoneBlocked: true);
        if (device != null && HasHResult(e, AudclntDeviceInUse)) return new RoomAudioException(L.F("Already used by another app: %@", device.Name));
        return new RoomAudioException(L.F(format, e.Message));
    }

    private static bool IsAccessDenied(Exception e)
    {
        for (var x = e; x != null; x = x.InnerException)
            if (x is UnauthorizedAccessException || x.HResult == EAccessDenied || (x is COMException c && c.ErrorCode == EAccessDenied)) return true;
        return false;
    }

    private static bool HasHResult(Exception e, int hr)
    {
        for (var x = e; x != null; x = x.InnerException)
            if (x.HResult == hr) return true;
        return false;
    }

    /// Whether Windows' privacy settings deny desktop apps the microphone (the consent store
    /// behind Settings › Privacy & security › Microphone). Only used to explain a silent recording.
    private static bool MicrophoneDeniedBySettings()
    {
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        static bool Denied(RegistryKey root, string path)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        return Denied(Registry.LocalMachine, store) || Denied(Registry.CurrentUser, store) || Denied(Registry.CurrentUser, store + @"\NonPackaged");
    }

    /// Paul Kellet's economy pink noise filter.
    private static float[] PinkNoise(int count, float amplitude)
    {
        float b0 = 0, b1 = 0, b2 = 0;
        var output = new float[Math.Max(count, 0)];
        var random = Random.Shared;
        for (var i = 0; i < output.Length; i++)
        {
            var white = random.NextSingle() * 2 - 1;
            b0 = 0.99765f * b0 + white * 0.0990460f;
            b1 = 0.96300f * b1 + white * 0.2965164f;
            b2 = 0.57000f * b2 + white * 1.0526913f;
            output[i] = (b0 + b1 + b2 + white * 0.1848f) * 0.11f * amplitude * 4;
        }
        var fade = Math.Min(output.Length / 4, 4800);
        for (var i = 0; i < fade; i++)
        {
            var g = (float)i / fade;
            output[i] *= g;
            output[output.Length - 1 - i] *= g;
        }
        return output;
    }
}
