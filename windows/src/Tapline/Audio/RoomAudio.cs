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

enum RoomPhase { Idle, CheckingLevel, Measuring, Analyzing, Identifying }

enum MeasurementQuality { Good, Fair, Noisy, Clipped }

/// A failure with a message for the user.
sealed class RoomAudioException(string message, bool microphoneBlocked = false) : Exception(message)
{
    /// Windows (or a muted / disconnected microphone) gave Tapline no microphone signal.
    public bool MicrophoneBlocked { get; } = microphoneBlocked;
}

/// Measures speakers in the room: plays a sine sweep (or pink noise for a level
/// check) on the output device while recording a microphone, then turns the
/// recording into a frequency response. A multichannel output (5.1 … 7.1.4) is
/// measured speaker by speaker in one run (`MeasureSpeakers`).
///
/// Equalizer APO processes every app, Tapline's own sweep included, so each run
/// takes the output device out of Equalizer APO's configuration while it plays
/// (`AppModel.BypassWhile`): the measurement sees the speakers and the room without Tapline's EQ,
/// effects or speaker corrections, and every signal comes out of the channel it was played on.
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
        /// The suggested microphone position it was taken at (0 … `PositionCount` - 1),
        /// null once all of them were measured.
        public int? Position { get; init; }

        public FrequencyResponse? LeftResponse => Left ?? (Speakers == RoomSpeakers.Left ? Response : null);
        public FrequencyResponse? RightResponse => Right ?? (Speakers == RoomSpeakers.Right ? Response : null);

        /// Clipped and noisy measurements are worth repeating.
        public MeasurementQuality Quality =>
            PeakDb > -1 ? MeasurementQuality.Clipped
            : SnrDb < 20 ? MeasurementQuality.Noisy
            : SnrDb < 30 ? MeasurementQuality.Fair
            : MeasurementQuality.Good;
    }

    /// One multichannel run at one microphone position: every speaker of the layout in turn.
    public sealed class SpeakerRun
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DateTime Date { get; init; } = DateTime.Now;
        /// The layout it was measured on; runs on another layout are not averaged.
        public required SpeakerLayout Layout { get; init; }
        /// The speakers measured, in channel order (responses with the microphone calibration applied).
        public required IReadOnlyList<MeasuredSpeaker> Speakers { get; init; }
        public required double PeakDb { get; init; }
        /// The suggested microphone position it was taken at (0 … `PositionCount` - 1),
        /// null once all of them were measured.
        public int? Position { get; init; }

        /// The worst speaker's signal to noise.
        public double SnrDb => Speakers.Count == 0 ? 0 : Speakers.Min(s => s.SnrDb);

        public MeasurementQuality Quality =>
            PeakDb > -1 ? MeasurementQuality.Clipped
            : SnrDb < 20 ? MeasurementQuality.Noisy
            : SnrDb < 30 ? MeasurementQuality.Fair
            : MeasurementQuality.Good;
    }

    /// One speaker of a multichannel run. `Arrival`: after the front left's, seconds (null: no timing
    /// chirp found); `OppositePolarity`: against the front left (null: unclear, always for LFE channels).
    public sealed record MeasuredSpeaker(int Channel, SpeakerPosition Position, FrequencyResponse Response, double? Arrival, bool? OppositePolarity,
                                         double SnrDb);

    /// One speaker over every run of a layout: the power average of its responses (and each run's, for
    /// the spread between positions), its mean arrival, opposite polarity if any run heard it (null if
    /// none could tell), the worst signal to noise.
    public sealed record SpeakerAverage(int Channel, SpeakerPosition Position, FrequencyResponse Response, IReadOnlyList<FrequencyResponse> Responses,
                                        double? Arrival, bool? OppositePolarity, double SnrDb);

    public const double SweepSeconds = 8.0;
    /// Each speaker's sweep in a multichannel run: shorter, as a 7.1.4 layout plays twelve of them.
    public const double SpeakerSweepSeconds = SpeakerSweepPlan.DefaultSweepSeconds;
    /// How long speaker identification plays its noise.
    public const double IdentifySeconds = 2.0;
    private const double PreRoll = 0.6;
    private const double Tail = 1.5;
    private const string MicrophoneSettingsUri = "ms-settings:privacy-microphone";

    private readonly List<Measurement> measurements = [];
    private readonly List<SpeakerRun> speakerRuns = [];
    private double runPeakDb = -120;
    // The multichannel run in progress: its layout, and a plan of it at a low rate for its timing.
    private SpeakerLayout? measuringLayout;
    private SpeakerSweepPlan? measuringPlan;

    public event Action? Changed;

    public RoomPhase Phase { get; private set; } = RoomPhase.Idle;
    /// 0…1 while measuring.
    public double Progress { get; private set; }
    /// Seconds into the running level check or measurement, and its length.
    public double ElapsedSeconds { get; private set; }
    public double RunSeconds { get; private set; }
    private RoomSpeakers measuringSpeakers = RoomSpeakers.Stereo;
    public IReadOnlyList<Measurement> Measurements => measurements;
    /// The output channel speaker identification plays on, null when it does not.
    public int? IdentifyingChannel { get; private set; }
    /// Live microphone level during a level check or measurement, dBFS.
    public double InputPeakDb { get; private set; } = -120;
    public double InputRmsDb { get; private set; } = -120;
    public FrequencyResponse? Calibration { get; private set; }
    public string? CalibrationName { get; private set; }
    public string? LastMessage { get; set; }
    /// The last run got no microphone signal, probably because of Windows' privacy settings.
    public bool MicrophoneBlocked { get; set; }

    public bool IsBusy => Phase != RoomPhase.Idle;

    public int SecondsLeft => (int)Math.Ceiling(Math.Max(0, RunSeconds - ElapsedSeconds));

    /// What the measurement in progress plays right now.
    public string? NowPlaying
    {
        get
        {
            if (Phase != RoomPhase.Measuring) return null;
            if (measuringLayout != null)
            {
                if (NowPlayingSpeaker is not { } now) return null;
                return now.Chirps ? L.T("Timing clicks") : L.F("%@ (%d of %d)", SpeakerName(now.Position), now.Index, now.Count);
            }
            switch (measuringSpeakers)
            {
                case RoomSpeakers.Left: return L.T("Left speaker");
                case RoomSpeakers.Right: return L.T("Right speaker");
                case RoomSpeakers.Both: return L.T("Both speakers");
            }
            // StereoSweepPlan's timeline: left sweep, gap, right sweep, gap, timing clicks.
            const double gap = StereoSweepPlan.Gap;
            const double rightStart = StereoSweepPlan.PreRoll + SweepSeconds + gap;
            const double clicksStart = rightStart + SweepSeconds + gap;
            if (ElapsedSeconds < rightStart - gap / 2) return L.T("Left speaker");
            if (ElapsedSeconds < clicksStart - gap / 2) return L.T("Right speaker");
            return L.T("Timing clicks");
        }
    }

    /// What the multichannel measurement in progress plays now: which speaker, whether its sweep or
    /// its timing click, and its place in the order (from 1).
    public (SpeakerPosition Position, bool Chirps, int Index, int Count, int Channel)? NowPlayingSpeaker
    {
        get
        {
            if (Phase != RoomPhase.Measuring || measuringPlan is not { } plan || measuringLayout is not { } layout) return null;
            if (plan.Playing(Math.Max(ElapsedSeconds, SpeakerSweepPlan.PreRoll)) is not { } now) return null;
            var channel = plan.Speakers[now.Speaker].Channel;
            if (channel >= layout.ChannelCount) return null;
            return (layout.Positions[channel], now.Chirps, now.Speaker + 1, plan.Speakers.Count, channel);
        }
    }

    /// The output channel speaker identification or a multichannel measurement plays on now.
    public int? PlayingChannel => Phase == RoomPhase.Identifying ? IdentifyingChannel : NowPlayingSpeaker?.Channel;

    /// A speaker's name for the UI: "Front left", "Top rear right"…
    public static string SpeakerName(SpeakerPosition p) => L.T(p switch
    {
        SpeakerPosition.FrontLeft => "Front left",
        SpeakerPosition.FrontRight => "Front right",
        SpeakerPosition.Center => "Front center",
        SpeakerPosition.Lfe => "Subwoofer (LFE)",
        SpeakerPosition.Lfe2 => "Second subwoofer (LFE 2)",
        SpeakerPosition.FrontLeftCenter => "Front left center",
        SpeakerPosition.FrontRightCenter => "Front right center",
        SpeakerPosition.WideLeft => "Front wide left",
        SpeakerPosition.WideRight => "Front wide right",
        SpeakerPosition.SurroundLeft => "Surround left",
        SpeakerPosition.SurroundRight => "Surround right",
        SpeakerPosition.RearLeft => "Rear surround left",
        SpeakerPosition.RearRight => "Rear surround right",
        SpeakerPosition.RearCenter => "Rear center",
        SpeakerPosition.TopFrontLeft => "Top front left",
        SpeakerPosition.TopFrontCenter => "Top front center",
        SpeakerPosition.TopFrontRight => "Top front right",
        SpeakerPosition.TopMiddleLeft => "Top middle left",
        SpeakerPosition.TopMiddleCenter => "Top middle center",
        SpeakerPosition.TopMiddleRight => "Top middle right",
        SpeakerPosition.TopRearLeft => "Top rear left",
        SpeakerPosition.TopRearCenter => "Top rear center",
        SpeakerPosition.TopRearRight => "Top rear right",
        _ => "Unknown",
    });

    // MARK: - Positions

    /// Suggested microphone positions: the listening spot, then about 20 cm to the
    /// left, to the right, in front and behind.
    public const int PositionCount = 5;

    public IReadOnlySet<int> MeasuredPositions =>
        measurements.Where(m => m.Position.HasValue).Select(m => m.Position!.Value).ToHashSet();

    /// The first suggested position not measured yet.
    public int? NextPosition
    {
        get
        {
            var taken = MeasuredPositions;
            for (var i = 0; i < PositionCount; i++)
                if (!taken.Contains(i)) return i;
            return null;
        }
    }

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

    // MARK: - Multichannel runs, positions and averages

    /// Every multichannel run, of any layout.
    public IReadOnlyList<SpeakerRun> AllSpeakerRuns => speakerRuns;

    public IReadOnlyList<SpeakerRun> SpeakerRuns(SpeakerLayout layout) => [.. speakerRuns.Where(r => r.Layout.Equals(layout))];

    public void RemoveSpeakerRun(Guid id)
    {
        speakerRuns.RemoveAll(r => r.Id == id);
        Changed?.Invoke();
    }

    public void ClearSpeakerRuns(SpeakerLayout layout)
    {
        speakerRuns.RemoveAll(r => r.Layout.Equals(layout));
        Changed?.Invoke();
    }

    public IReadOnlySet<int> MeasuredSpeakerPositions(SpeakerLayout layout) =>
        SpeakerRuns(layout).Where(r => r.Position.HasValue).Select(r => r.Position!.Value).ToHashSet();

    /// The first suggested position without a run of `layout` yet.
    public int? NextSpeakerPosition(SpeakerLayout layout)
    {
        var taken = MeasuredSpeakerPositions(layout);
        for (var i = 0; i < PositionCount; i++)
            if (!taken.Contains(i)) return i;
        return null;
    }

    /// Every measured speaker of `layout`, in channel order.
    public IReadOnlyList<SpeakerAverage> SpeakerAverages(SpeakerLayout layout)
    {
        var runs = SpeakerRuns(layout);
        var result = new List<SpeakerAverage>();
        foreach (var channel in layout.SpeakerChannels)
        {
            var measured = runs.SelectMany(r => r.Speakers.Where(s => s.Channel == channel).Take(1)).ToList();
            if (RoomResponse.Average(measured.Select(s => s.Response).ToList()) is not { } response) continue;
            var arrivals = measured.Where(s => s.Arrival.HasValue).Select(s => s.Arrival!.Value).ToList();
            var polarities = measured.Where(s => s.OppositePolarity.HasValue).Select(s => s.OppositePolarity!.Value).ToList();
            result.Add(new SpeakerAverage(channel, layout.Positions[channel], response, [.. measured.Select(s => s.Response)],
                                          arrivals.Count == 0 ? null : arrivals.Average(),
                                          polarities.Count == 0 ? null : polarities.Contains(true),
                                          measured.Min(s => s.SnrDb)));
        }
        return result;
    }

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
    public Task CheckLevel(AudioDevice output, AudioDevice input, int channel, RoomSpeakers speakers) =>
        CheckLevel(output, input, channel, (rate, _, _) =>
        {
            var noise = PinkNoise((int)(LevelCheckSeconds * rate), 0.12f);
            var silent = new float[noise.Length];
            return Stereo(speakers == RoomSpeakers.Right ? silent : noise, speakers == RoomSpeakers.Left ? silent : noise);
        });

    /// The level check of a multichannel output: the noise on `outputChannels` (its front left and right).
    public Task CheckLevel(AudioDevice output, AudioDevice input, int channel, IReadOnlyList<int> outputChannels) =>
        CheckLevel(output, input, channel, (rate, _, available) =>
        {
            if (outputChannels.Count == 0 || outputChannels.Any(c => c < 0 || c >= available)) throw new RoomAudioException(ChannelsChangedMessage);
            var noise = PinkNoise((int)(LevelCheckSeconds * rate), 0.12f);
            return [.. outputChannels.Select(c => (c, noise))];
        });

    private const double LevelCheckSeconds = 3.0;

    private async Task CheckLevel(AudioDevice output, AudioDevice input, int channel, SignalMaker signals)
    {
        if (IsBusy) return;
        MicrophoneBlocked = false;
        Phase = RoomPhase.CheckingLevel;
        Changed?.Invoke();
        try
        {
            await Record(output, input, channel, 0.3, signals);
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
        measuringSpeakers = speakers;
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
        var (recording, inputRate, outputRate) = await Record(output, input, channel, Tail, (rate, micRate, _) =>
        {
            var sweep = new SineSweep(rate, SweepSeconds, endFrequency: SineSweep.EndFrequencyFor(rate, micRate)).Signal;
            var preRoll = (int)(PreRoll * rate);
            var padded = new float[preRoll + sweep.Length];
            sweep.CopyTo(padded, preRoll);
            var silent = new float[padded.Length];
            return Stereo(speakers == RoomSpeakers.Right ? silent : padded, speakers == RoomSpeakers.Left ? silent : padded);
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
        Add(new Measurement { Speakers = speakers, Response = response, PeakDb = peakDb, SnrDb = snrDb, Position = NextPosition });
        LastMessage = ResultMessage(peakDb, snrDb);
    }

    /// One recording: a sweep per speaker, then left / right / left timing chirps.
    private async Task MeasureStereo(AudioDevice output, AudioDevice input, int channel)
    {
        const double sweepSeconds = SweepSeconds;
        var (recording, inputRate, outputRate) = await Record(output, input, channel, 0.4, (rate, micRate, _) =>
        {
            var (left, right) = new StereoSweepPlan(rate, sweepSeconds, SineSweep.EndFrequencyFor(rate, micRate)).Render();
            return Stereo(left, right);
        });

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
            Position = NextPosition,
        });
        LastMessage = ResultMessage(analysis.PeakDb, snr);
    }

    // MARK: - Multichannel

    /// Measures every known speaker of `layout` (the output's, one position per channel) in one run,
    /// in channel order: a sweep on each speaker in turn, then timing clicks, with the front left as
    /// the timing reference and a low click for LFE channels. The run is stored for the next suggested
    /// position of that layout.
    public async Task MeasureSpeakers(AudioDevice output, AudioDevice input, int channel, SpeakerLayout layout)
    {
        if (IsBusy) return;
        var speakers = layout.SpeakerChannels.Select(c => new SpeakerSweepPlan.Speaker(c, layout.Positions[c].IsLfe())).ToList();
        if (speakers.Count == 0) return;
        var reference = layout.ReferenceChannel is { } r ? Math.Max(0, speakers.FindIndex(s => s.Channel == r)) : 0;
        LastMessage = null;
        MicrophoneBlocked = false;
        // Only for its timing (what plays when): a low rate is quick to make.
        measuringPlan = new SpeakerSweepPlan(8000, speakers, reference, SpeakerSweepSeconds);
        measuringLayout = layout;
        Phase = RoomPhase.Measuring;
        Progress = 0;
        Changed?.Invoke();
        try
        {
            await MeasureSpeakers(output, input, channel, layout, speakers, reference);
        }
        catch (Exception e)
        {
            Report(e);
        }
        finally
        {
            Phase = RoomPhase.Idle;
            Progress = 0;
            measuringLayout = null;
            measuringPlan = null;
            Changed?.Invoke();
        }
    }

    private async Task MeasureSpeakers(AudioDevice output, AudioDevice input, int channel, SpeakerLayout layout,
                                       List<SpeakerSweepPlan.Speaker> speakers, int reference)
    {
        const double sweepSeconds = SpeakerSweepSeconds;
        var (recording, inputRate, outputRate) = await Record(output, input, channel, 0.4, (rate, micRate, outputChannels) =>
        {
            if (speakers.Any(s => s.Channel >= outputChannels)) throw new RoomAudioException(ChannelsChangedMessage);
            var signals = new SpeakerSweepPlan(rate, speakers, reference, sweepSeconds, SineSweep.EndFrequencyFor(rate, micRate)).Render();
            return speakers.Select((s, i) => (s.Channel, signals[i])).ToList();
        });

        Phase = RoomPhase.Analyzing;
        Changed?.Invoke();
        var calibration = Calibration;
        var analysis = await Task.Run(() => SpeakerSweepPlan.Analyze(recording, inputRate, outputRate, speakers, reference, sweepSeconds));
        if (analysis == null)
        {
            LastMessage = L.T("The measurement failed. Check that the microphone hears the speakers.");
            return;
        }
        var measured = analysis.Speakers.Select(result => new MeasuredSpeaker(
            result.Channel, layout.Positions[result.Channel],
            calibration != null ? RoomResponse.ApplyCalibration(result.Response, calibration) : result.Response,
            result.Arrival, result.OppositePolarity, result.SnrDb)).ToList();
        var run = new SpeakerRun { Layout = layout, Speakers = measured, PeakDb = analysis.PeakDb, Position = NextSpeakerPosition(layout) };
        speakerRuns.Add(run);
        LastMessage = ResultMessage(analysis.PeakDb, run.SnrDb);
        Changed?.Invoke();
    }

    /// Plays two seconds of pink noise on one channel of `output`, without the microphone, to hear
    /// which speaker it comes from (the wiring check). As for a measurement, the output leaves
    /// Equalizer APO meanwhile, so swapped channels or a correction's delay cannot move or hold back
    /// the noise. `cancel` stops it early.
    public async Task IdentifySpeaker(AudioDevice output, int outputChannel, CancellationToken cancel = default)
    {
        if (IsBusy || cancel.IsCancellationRequested) return;
        LastMessage = null;
        MicrophoneBlocked = false;
        Phase = RoomPhase.Identifying;
        IdentifyingChannel = outputChannel;
        Changed?.Invoke();
        try
        {
            await Record(output, null, 0, 0.2, (rate, _, outputChannels) =>
            {
                if (outputChannel < 0 || outputChannel >= outputChannels) throw new RoomAudioException(ChannelsChangedMessage);
                return [(outputChannel, PinkNoise((int)(IdentifySeconds * rate), 0.12f))];
            }, cancel);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Report(e);
        }
        finally
        {
            Phase = RoomPhase.Idle;
            IdentifyingChannel = null;
            Changed?.Invoke();
        }
    }

    private static string ChannelsChangedMessage =>
        L.T("The output's channels changed. Check its speaker setup in Windows' Sound settings, then try again.");

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

    /// Makes the signals of a run from the output's and the input's sample rates and the output's
    /// channel count: a signal per output channel (0-based); the other channels play silence.
    private delegate IReadOnlyList<(int Channel, float[] Signal)> SignalMaker(double outputRate, double inputRate, int outputChannels);

    private static IReadOnlyList<(int Channel, float[] Signal)> Stereo(float[] left, float[] right) => [(0, left), (1, right)];

    /// Runs one playback + recording with the output taken out of Equalizer APO. Without an input, only plays.
    private async Task<(float[] Recording, double InputRate, double OutputRate)> Record(AudioDevice output, AudioDevice? input, int channel, double extraSeconds,
                                                                                        SignalMaker signals, CancellationToken cancel = default)
    {
        (float[] Recording, double InputRate, double OutputRate) result = (Array.Empty<float>(), 0, 0);
        await model.BypassWhile(output, async () => result = await Run(output, input, channel, extraSeconds, signals, cancel));
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

    /// Plays each signal in its channel of the device's mix format (32-bit float), silence in the
    /// other channels and after the end; a mono device plays whichever signal sounds. Read on
    /// WasapiOut's playback thread.
    private sealed class SignalProvider(IReadOnlyList<(int Channel, float[] Signal)> signals, int count, WaveFormat format) : IWaveProvider
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
                foreach (var (channel, signal) in signals)
                {
                    if (p >= signal.Length) continue;
                    var v = signal[p];
                    if (channels == 1)
                    {
                        if (v == 0) continue;
                        samples[i] = v;
                        break;
                    }
                    if (channel >= 0 && channel < channels) samples[i * channels + channel] = v;
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

    /// Plays the signals made by `signals` (given the output's mix-format sample rate, the input's rate
    /// and the output's channel count) on `output` and records one channel of `input` for the signals'
    /// length plus `extraSeconds`. Returns the recording, the input rate and the output rate. Without
    /// an input it only plays, and returns an empty recording. `cancel` stops playing early.
    private async Task<(float[] Recording, double InputRate, double OutputRate)> Run(AudioDevice output, AudioDevice? input, int channel, double extraSeconds,
                                                                                     SignalMaker signals, CancellationToken cancel)
    {
        MMDevice? outDevice = null, inDevice = null;
        WasapiOut? player = null;
        WasapiCapture? capture = null;
        runPeakDb = -120;
        try
        {
            outDevice = model.Devices.Open(output) ?? throw new RoomAudioException(L.F("Could not open the audio devices: %@", output.Name));
            if (input != null)
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
            WaveFormat? inFormat = null;
            if (inDevice != null)
            {
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
            }
            var inRate = inFormat?.SampleRate ?? outFormat.SampleRate;

            // The signals take a moment to compute (the sweep normalises itself with a long FFT).
            IReadOnlyList<(int Channel, float[] Signal)> played;
            try
            {
                played = await Task.Run(() => signals(outFormat.SampleRate, inRate, outFormat.Channels));
            }
            catch (RoomAudioException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log(e);
                throw new RoomAudioException(L.T("Could not prepare the test signal."));
            }
            var count = played.Count == 0 ? 0 : played.Max(x => x.Signal.Length);
            if (count == 0) throw new RoomAudioException(L.T("Could not prepare the test signal."));
            var seconds = count / (double)outFormat.SampleRate + extraSeconds;

            var store = new Recorder(inFormat != null ? (int)((seconds + 1) * inFormat.SampleRate) : 0);
            var inputFailure = new FailureBox();
            var outputFailure = new FailureBox();
            if (capture != null && inFormat != null)
            {
                var inChannels = inFormat.Channels;
                var bytesPerSample = inFormat.BitsPerSample / 8;
                var isFloat = LoopbackMonitor.IsFloat(inFormat);
                var ch = Math.Clamp(channel, 0, inChannels - 1);
                capture.DataAvailable += (_, e) => store.Append(e.Buffer, e.BytesRecorded, inChannels, bytesPerSample, isFloat, ch);
                capture.RecordingStopped += (_, e) => { if (e.Exception != null) inputFailure.Set(e.Exception); };
            }
            player.PlaybackStopped += (_, e) => { if (e.Exception != null) outputFailure.Set(e.Exception); };

            try
            {
                player.Init(new SignalProvider(played, count, outFormat));
            }
            catch (Exception e)
            {
                throw Wrap(e, "Could not start the audio devices: %@", output, isInput: false);
            }
            // Recording first, so it catches the start of the signal.
            if (capture != null)
            {
                try
                {
                    capture.StartRecording();
                }
                catch (Exception e)
                {
                    throw Wrap(e, "Could not start the audio devices: %@", input, isInput: true);
                }
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
            RunSeconds = seconds;
            ElapsedSeconds = 0;
            while (clock.Elapsed.TotalSeconds < seconds && !cancel.IsCancellationRequested)
            {
                await Task.Delay(100);
                if (inputFailure.Value is { } fi) throw Wrap(fi, "Could not start the audio devices: %@", input, isInput: true);
                if (outputFailure.Value is { } fo) throw Wrap(fo, "Could not start the audio devices: %@", output, isInput: false);
                if (capture != null)
                {
                    var (peak, rms) = store.TakeLevels();
                    InputPeakDb = 20 * Math.Log10(Math.Max(peak, 1e-9));
                    InputRmsDb = 20 * Math.Log10(Math.Max(rms, 1e-9));
                    runPeakDb = Math.Max(runPeakDb, InputPeakDb);
                }
                ElapsedSeconds = clock.Elapsed.TotalSeconds;
                if (Phase == RoomPhase.Measuring) Progress = Math.Min(1, ElapsedSeconds / seconds);
                Changed?.Invoke();
            }

            // Real microphones always hiss a little: digital silence means Windows withheld the
            // microphone (privacy settings) or the device is muted.
            if (capture != null && store.MaxPeak == 0)
            {
                throw new RoomAudioException(MicrophoneDeniedBySettings() ? MicrophoneAccessMessage : L.T(
                    "The microphone sent only silence. Check that it is connected and not muted, and that Windows lets desktop apps use it (Settings › Privacy & security › Microphone)."),
                    microphoneBlocked: true);
            }
            return (store.All(), inRate, outFormat.SampleRate);
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

    /// A synthetic room (modes, a cancellation dip, a slightly louder and farther right
    /// speaker) measured at four positions, for snapshots.
    public void ApplyDemo()
    {
        if (measurements.Count > 0) return;
        var grid = EqFitter.Grid;
        FrequencyResponse Speaker(EqBand extra, double level, ulong seed, double shift)
        {
            var room = ResponseCurve.ResponseDb(
            [
                new EqBand(FilterType.HighPass, 38, q: 0.8),
                new EqBand(FilterType.Peak, 47, 8 + shift, 5),
                new EqBand(FilterType.Peak, 112, -9 - shift, 4),
                new EqBand(FilterType.Peak, 176, 5 - shift / 2, 6),
                new EqBand(FilterType.Peak, 300, -3, 3),
                new EqBand(FilterType.HighShelf, 9000, -2, 0.7),
                extra,
            ], grid, 48_000);
            var levels = new double[room.Length];
            for (var i = 0; i < room.Length; i++)
            {
                seed = unchecked(seed * 6364136223846793005UL + 1442695040888963407UL);
                var wiggle = ((seed >> 33) / (double)(1UL << 31) - 0.5) * (grid[i] > 1000 ? 2.0 : 0.6);
                levels[i] = level + room[i] + wiggle;
            }
            return new FrequencyResponse([.. grid], levels);
        }
        // The modes change a little from one position to the next; the third was taken in a noisier moment.
        (double Shift, double Level, double Snr, double Peak)[] spots = [(0, 0, 52, -14), (3, -0.4, 47, -15), (-2.5, 0.3, 27, -18), (1.5, -0.2, 50, -14)];
        for (var i = 0; i < spots.Length; i++)
        {
            var spot = spots[i];
            var seed = (ulong)i * 101;
            var left = Speaker(new EqBand(FilterType.Peak, 68, 3, 4), 82 + spot.Level, 42 + seed, spot.Shift);
            var right = Speaker(new EqBand(FilterType.Peak, 68, -4, 4), 82.9 + spot.Level, 7 + seed, spot.Shift);
            measurements.Add(new Measurement
            {
                Speakers = RoomSpeakers.Stereo,
                Response = RoomResponse.Average([left, right]) ?? left,
                Left = left,
                Right = right,
                ArrivalDifference = 0.00011,
                OppositePolarity = false,
                PeakDb = spot.Peak,
                SnrDb = spot.Snr,
                Position = i,
            });
        }
        MeasurementsChanged();
    }

    /// The layout of the multichannel demo: a 7.1.4 output in Windows' channel order
    /// (L R C LFE Lrs Rrs Ls Rs Ltf Rtf Ltr Rtr).
    public static SpeakerLayout DemoLayout { get; } = SpeakerLayout.FromChannelMask(0x2D63F, 12);

    /// A synthetic home cinema measured at four positions, for snapshots (as on macOS): every speaker
    /// with its own room, distance and level, a subwoofer, and one height speaker wired backwards.
    public void ApplySpeakerDemo(SpeakerLayout layout)
    {
        if (SpeakerRuns(layout).Count > 0) return;
        var grid = EqFitter.Grid;
        double[] levelOffsets = [0, 0.6, 1.8, 9.5, -1.2, -0.8, -2.1, -1.7, -3.0, -2.6, -3.4, -2.9, -1, -1, -2, -2];
        FrequencyResponse Response(SpeakerPosition position, int channel, int spot)
        {
            double[] shifts = [0, 2.5, -2, 1.5];
            var shift = shifts[spot % 4];
            List<EqBand> bands;
            if (position.IsLfe())
            {
                bands =
                [
                    new EqBand(FilterType.HighPass, 24, q: 0.8), new EqBand(FilterType.LowPass, 95, q: 0.7),
                    new EqBand(FilterType.LowPass, 110, q: 0.7),
                    new EqBand(FilterType.Peak, 42 + spot, 7 + shift, 5), new EqBand(FilterType.Peak, 68, -6 - shift, 4),
                ];
            }
            else
            {
                var small = position.Group() != SpeakerGroup.Front;
                bands =
                [
                    new EqBand(FilterType.HighPass, small ? 75 : 40, q: 0.8),
                    new EqBand(FilterType.Peak, 48 + channel, 6 + shift, 5),
                    new EqBand(FilterType.Peak, 105 + 9 * channel, -7 - shift, 4),
                    new EqBand(FilterType.Peak, 170 + 6 * channel, 4 - shift / 2, 6),
                    new EqBand(FilterType.HighShelf, 9000, position.IsHeight() ? -4 : -2, 0.7),
                ];
            }
            var room = ResponseCurve.ResponseDb(bands, grid, 48_000);
            var seed = (ulong)(channel * 31 + spot * 7 + 3);
            var level = 82 + levelOffsets[channel % 16];
            var levels = new double[room.Length];
            for (var i = 0; i < room.Length; i++)
            {
                seed = unchecked(seed * 6364136223846793005UL + 1442695040888963407UL);
                levels[i] = level + room[i] + ((seed >> 33) / (double)(1UL << 31) - 0.5) * (grid[i] > 1000 ? 2.0 : 0.6);
            }
            return new FrequencyResponse([.. grid], levels);
        }
        // Metres farther than the front left (the subwoofer also lags by its low-pass filter).
        static double Distance(SpeakerPosition p) => p switch
        {
            SpeakerPosition.FrontLeft => 0,
            SpeakerPosition.FrontRight => 0.05,
            SpeakerPosition.Center => -0.12,
            SpeakerPosition.Lfe or SpeakerPosition.Lfe2 => 2.4,
            SpeakerPosition.SurroundLeft or SpeakerPosition.SurroundRight => -0.9,
            SpeakerPosition.RearLeft or SpeakerPosition.RearRight => -0.4,
            SpeakerPosition.WideLeft or SpeakerPosition.WideRight => -0.3,
            _ => p.IsHeight() ? -0.6 : 0,
        };
        (double Snr, double Peak)[] spots = [(51, -14), (47, -15), (28, -18), (49, -14)];
        for (var i = 0; i < spots.Length; i++)
        {
            var spot = spots[i];
            var measured = layout.SpeakerChannels.Select(c =>
            {
                var p = layout.Positions[c];
                return new MeasuredSpeaker(c, p, Response(p, c, i), (Distance(p) + 0.01 * (i % 2)) / SpeakerAlignment.SpeedOfSound,
                                           p.IsLfe() ? null : p == SpeakerPosition.TopRearRight, spot.Snr - (p.IsLfe() ? 3 : 0));
            }).ToList();
            speakerRuns.Add(new SpeakerRun { Date = DateTime.Today.AddHours(20).AddMinutes(10 + 2 * i), Layout = layout, Speakers = measured, PeakDb = spot.Peak, Position = i });
        }
        Changed?.Invoke();
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
