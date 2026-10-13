using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Tapline.Core;

namespace Tapline.Core.Tests;

/// The Windows port of VirtualSpeakerTests.swift: WAV reading, HeSuVi / Impulcifer layouts,
/// trimming, resampling and the loudness gain, and the bundled sets.
public class WavFileTests
{
    /// A RIFF WAVE file with the given format chunk and raw sample bytes.
    internal static byte[] Wav(ushort tag, ushort channels, ushort bits, byte[] samples, uint rate = 48_000, bool extensible = false, bool extraChunk = false)
    {
        var d = new List<byte>();
        void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); d.AddRange(b); }
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); d.AddRange(b); }
        var align = (ushort)(channels * bits / 8);
        d.AddRange("RIFF"u8.ToArray()); U32(0);
        d.AddRange("WAVE"u8.ToArray());
        if (extraChunk)
        {
            // An odd-sized chunk before the format: it is padded to an even length.
            d.AddRange("LIST"u8.ToArray()); U32(3); d.AddRange(new byte[] { 1, 2, 3, 0 });
        }
        d.AddRange("fmt "u8.ToArray()); U32(extensible ? 40u : 16u);
        U16(extensible ? (ushort)0xFFFE : tag); U16(channels); U32(rate); U32(rate * align); U16(align); U16(bits);
        if (extensible)
        {
            U16(22); U16(bits); U32(0);
            U16(tag); d.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }
        d.AddRange("data"u8.ToArray()); U32((uint)samples.Length);
        d.AddRange(samples);
        return d.ToArray();
    }

    [Fact]
    public void ReadsSixteenBitPcm()
    {
        var bytes = new short[] { 0, 16384, -32768, 32767 }.SelectMany(v => BitConverter.GetBytes(v)).ToArray();
        var file = WavFile.Parse(Wav(1, 2, 16, bytes, extraChunk: true));
        Assert.Equal(48_000.0, file.SampleRate);
        Assert.Equal(new float[] { 0, -1 }, file.Channels[0]);
        Assert.Equal(new float[] { 0.5f, 32767 / 32768f }, file.Channels[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsTwentyFourBitPcm(bool extensible)
    {
        // 0x400000 = 0.5, 0xC00000 = -0.5
        var file = WavFile.Parse(Wav(1, 1, 24, [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0], extensible: extensible));
        Assert.Equal(new float[] { 0.5f, -0.5f }, file.Channels[0]);
    }

    [Fact]
    public void ReadsFloatAndEightBit()
    {
        var floats = new[] { 0.25f, -0.75f, float.NaN }.SelectMany(v => BitConverter.GetBytes(v)).ToArray();
        Assert.Equal(new float[] { 0.25f, -0.75f, 0 }, WavFile.Parse(Wav(3, 1, 32, floats)).Channels[0]);
        Assert.Equal(new float[] { 0.5f }, WavFile.Parse(Wav(3, 1, 64, BitConverter.GetBytes(0.5))).Channels[0]);
        Assert.Equal(new float[] { 0, 0.5f, -1 }, WavFile.Parse(Wav(1, 1, 8, [128, 192, 0])).Channels[0]);
    }

    [Fact]
    public void RoundTripsFloatFiles()
    {
        float[][] channels = [[0.1f, 0.2f, -0.3f], [1, 0, -1], [0, 0, 0.5f], [0.25f, 0.5f, 0.75f]];
        var again = WavFile.Parse(new WavFile(44_100, channels).Encode());
        Assert.Equal(44_100.0, again.SampleRate);
        for (var c = 0; c < 4; c++) Assert.Equal(channels[c], again.Channels[c]);
    }

    [Fact]
    public void ReadsATruncatedDataChunkAsFarAsItGoes()
    {
        var data = Wav(1, 1, 16, [0x00, 0x40, 0x00, 0x20]);
        Assert.Equal(new float[] { 0.5f }, WavFile.Parse(data.AsSpan(0, data.Length - 2)).Channels[0]);
    }

    [Fact]
    public void RejectsWhatItCannotRead()
    {
        Assert.Equal(WavError.NotWav, Assert.Throws<WavFormatException>(() => WavFile.Parse(Encoding.ASCII.GetBytes("hello world!"))).Error);
        Assert.Equal(WavError.UnsupportedFormat, Assert.Throws<WavFormatException>(() => WavFile.Parse(Wav(2, 1, 4, [0, 0]))).Error);
        Assert.Equal(WavError.Empty, Assert.Throws<WavFormatException>(() => WavFile.Parse(Wav(1, 2, 16, []))).Error);
    }
}

public class BinauralLayoutTests
{
    /// A file whose channel i holds the constant (i + 1) / 100.
    private static WavFile Marked(int channels, int frames = 8) =>
        new(48_000, Enumerable.Range(0, channels).Select(c => Enumerable.Repeat((c + 1) / 100f, frames).ToArray()).ToArray());

    private static int[] Markers(BinauralIrs irs) => irs.Responses.Select(r => (int)Math.Round(r[0] * 100)).ToArray();

    [Fact]
    public void HesuviFourteenChannels()
    {
        // FL-L = 1, FL-R = 2, FR-R = 8, FR-L = 9 (1-based, as HeSuVi numbers them).
        var irs = BinauralIrs.FromWav(Marked(14));
        Assert.Equal(BinauralLayout.Hesuvi14, irs.Layout);
        Assert.Equal([1, 2, 9, 8], Markers(irs));
    }

    [Fact]
    public void HesuviSevenChannelsMirrorTheLeftSpeaker()
    {
        var irs = BinauralIrs.FromWav(Marked(7));
        Assert.Equal(BinauralLayout.Hesuvi7, irs.Layout);
        Assert.Equal([1, 2, 2, 1], Markers(irs));
    }

    [Fact]
    public void ImpulciferAndFourChannelFiles()
    {
        var impulcifer = BinauralIrs.FromWav(Marked(16));
        Assert.Equal(BinauralLayout.Impulcifer16, impulcifer.Layout);
        Assert.Equal([1, 2, 3, 4], Markers(impulcifer));
        var four = BinauralIrs.FromWav(Marked(4));
        Assert.Equal(BinauralLayout.FourChannel, four.Layout);
        Assert.Equal([1, 2, 3, 4], Markers(four));
    }

    [Fact]
    public void OneStereoSpeakerIsMirrored()
    {
        var fromLeft = BinauralIrs.FromWav(new WavFile(48_000, [[0.9f, 0.1f], [0.2f, 0.05f]]));
        Assert.Equal(BinauralLayout.MirroredStereo, fromLeft.Layout);
        Assert.Equal([0.9f, 0.1f], fromLeft.Responses[0]);
        Assert.Equal([0.2f, 0.05f], fromLeft.Responses[1]);
        Assert.Equal([0.2f, 0.05f], fromLeft.Responses[2]);
        Assert.Equal([0.9f, 0.1f], fromLeft.Responses[3]);
        // A right speaker: louder at the right ear.
        var fromRight = BinauralIrs.FromWav(new WavFile(48_000, [[0.2f, 0.05f], [0.9f, 0.1f]]));
        for (var i = 0; i < 4; i++) Assert.Equal(fromLeft.Responses[i], fromRight.Responses[i]);
    }

    [Fact]
    public void TwoStereoFilesInEitherOrder()
    {
        var left = new WavFile(48_000, [[0.9f], [0.2f]]);
        var right = new WavFile(48_000, [[0.3f], [0.8f]]);
        var a = BinauralIrs.FromFiles([left, right]);
        var b = BinauralIrs.FromFiles([right, left]);
        Assert.Equal(BinauralLayout.StereoPair, a.Layout);
        Assert.Equal([0.9f, 0.2f, 0.3f, 0.8f], a.Responses.Select(r => r[0]));
        Assert.Equal(a.Responses.Select(r => r[0]), b.Responses.Select(r => r[0]));
        Assert.Equal(BinauralImportError.SameSide, Assert.Throws<BinauralImportException>(() => BinauralIrs.FromFiles([left, left])).Error);
        var other = new WavFile(44_100, [[0.3f], [0.8f]]);
        Assert.Equal(BinauralImportError.SampleRateMismatch, Assert.Throws<BinauralImportException>(() => BinauralIrs.FromFiles([left, other])).Error);
    }

    [Fact]
    public void RejectsOtherLayouts()
    {
        var six = Assert.Throws<BinauralImportException>(() => BinauralIrs.FromWav(Marked(6)));
        Assert.Equal((BinauralImportError.UnsupportedChannelCount, 6), (six.Error, six.Count));
        Assert.Equal(BinauralImportError.UnsupportedChannelCount, Assert.Throws<BinauralImportException>(() => BinauralIrs.FromWav(Marked(1))).Error);
        Assert.Equal(BinauralImportError.WrongFileCount, Assert.Throws<BinauralImportException>(() => BinauralIrs.FromFiles([Marked(2), Marked(2), Marked(2)])).Error);
        var silent = new WavFile(48_000, Enumerable.Range(0, 14).Select(_ => new float[2]).ToArray());
        Assert.Equal(BinauralImportError.Silent, Assert.Throws<BinauralImportException>(() => BinauralIrs.FromWav(silent)).Error);
    }

    [Fact]
    public void TrimmingKeepsTheDelaysBetweenEars()
    {
        static float[] Pulse(int at, int total) => Enumerable.Range(0, total).Select(i => i == at ? 1f : i == at + 1 ? -0.5f : 0f).ToArray();
        var irs = new BinauralIrs(48_000, [Pulse(500, 4000), Pulse(530, 4000), Pulse(540, 4000), Pulse(505, 4000)]);
        var trimmed = irs.Trimmed();
        // 1 ms before the first onset stays; the tail ends after the last sound.
        Assert.Equal(48, Array.IndexOf(trimmed.Responses[0], 1f));
        Assert.Equal(78, Array.IndexOf(trimmed.Responses[1], 1f));
        Assert.Equal(48 + 40 + 2, trimmed.Length);
        var cut = irs.Trimmed(maxLength: 40);
        Assert.Equal(40, cut.Length);
        Assert.All(cut.Responses, r => Assert.Equal(0f, r[^1]));
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(96_000)]
    [InlineData(192_000)]
    public void ResamplingKeepsTheGain(double target)
    {
        // A smooth pulse: its sum is the filter's gain at DC.
        var pulse = Enumerable.Range(0, 64).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 63))).ToArray();
        var irs = new BinauralIrs(48_000, [pulse, pulse, pulse, pulse]);
        var r = irs.Resampled(target);
        Assert.Equal(target, r.SampleRate);
        Assert.InRange((double)(r.Responses[0].Sum() / pulse.Sum()), 0.99, 1.01);
        Assert.Same(irs, irs.Resampled(48_000));
    }

    [Fact]
    public void ResamplingKeepsThePitch()
    {
        // A 1 kHz tone keeps its period: 96 samples at 96 kHz. (The phase keeps the zero crossings off the sample points.)
        var tone = Enumerable.Range(0, 4800).Select(i => (float)Math.Sin(2 * Math.PI * 1000 * i / 48_000.0 + 0.5)).ToArray();
        var up = new BinauralIrs(48_000, [tone, tone, tone, tone]).Resampled(96_000).Responses[0];
        var crossings = Enumerable.Range(1000, 7600).Where(i => up[i - 1] < 0 && up[i] >= 0).ToList();
        var period = (double)(crossings[^1] - crossings[0]) / (crossings.Count - 1);
        Assert.InRange(period, 95.99, 96.01);
    }

    [Fact]
    public void LoudnessGain()
    {
        Assert.Equal(1, new BinauralIrs(48_000, [[1f], [0f], [0f], [1f]]).LoudnessGain, 4);
        Assert.Equal(0.5, new BinauralIrs(48_000, [[2f], [0f], [0f], [2f]]).LoudnessGain, 4);
        // Full crossfeed: mono content doubles (+6 dB), wide content +3 dB; the gain splits it.
        Assert.Equal(Math.Pow(4.0 * 2.0, -0.25), new BinauralIrs(48_000, [[1f], [1f], [1f], [1f]]).LoudnessGain, 4);
    }

    [Fact]
    public void LayoutsSerializeAsOnTheMac()
    {
        Assert.Equal("\"hesuvi14\"", JsonSerializer.Serialize(BinauralLayout.Hesuvi14, TaplineJson.Options));
        Assert.Equal("\"mirroredStereo\"", JsonSerializer.Serialize(BinauralLayout.MirroredStereo, TaplineJson.Options));
    }

    /// The sets shipped with both apps (Tapline/Resources/VirtualSpeakers).
    internal static BinauralIrs Bundled(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Tapline", "Resources", "VirtualSpeakers", name + ".wav"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return BinauralIrs.FromWav(WavFile.Parse(File.ReadAllBytes(Path.Combine(dir!.FullName, "Tapline", "Resources", "VirtualSpeakers", name + ".wav"))));
    }

    [Theory]
    [InlineData("SADIE-II-KU100")]
    [InlineData("SADIE-II-KU100-Room")]
    public void BundledSetsAreFrontSpeakers(string name)
    {
        var irs = Bundled(name);
        Assert.Equal(BinauralLayout.FourChannel, irs.Layout);
        Assert.Equal(48_000.0, irs.SampleRate);
        // Each speaker is louder at the ear on its side, and reaches it first.
        var e = irs.Responses.Select(BinauralIrs.Energy).ToArray();
        Assert.True(e[0] > 4 * e[1] && e[3] > 4 * e[2]);
        static int Onset(float[] x) { var peak = x.Max(MathF.Abs); return Array.FindIndex(x, v => MathF.Abs(v) > 0.1f * peak); }
        Assert.True(Onset(irs.Responses[0]) < Onset(irs.Responses[1]));
        Assert.True(Onset(irs.Responses[3]) < Onset(irs.Responses[2]));
        Assert.InRange(irs.LoudnessGain, 0.3, 3);
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void SpeakerFilesAreMonoAtTheDeviceRate(double rate)
    {
        var irs = Bundled("SADIE-II-KU100-Room");
        var files = VirtualSpeakerFiles.Build(irs, EffectsSettings.DefaultVirtualSpeakers, rate);
        Assert.StartsWith("sadie-ku100-room-", files[0].Name);
        Assert.EndsWith("-FL-L.wav", files[0].Name);
        Assert.Contains($"-{(int)rate}-", files[0].Name);
        var prepared = irs.Prepared(rate);
        var gain = (float)prepared.LoudnessGain;
        for (var i = 0; i < 4; i++)
        {
            var wav = WavFile.Parse(files[i].Data);
            Assert.Equal(rate, wav.SampleRate);
            Assert.Single(wav.Channels);
            Assert.True(wav.FrameCount <= BinauralIrs.MaxLength);
            Assert.Equal(prepared.Responses[i].Select(v => v * gain).ToArray(), wav.Channels[0]);
        }
        var reference = VirtualSpeakerFiles.Reference(files);
        Assert.Equal(VirtualSpeakerFiles.Folder + @"\" + files[3].Name, reference.FrontRightToRight);
        Assert.Equal("a_b-48000", VirtualSpeakerFiles.Stem("a/b", 48_000));
    }
}

public class VirtualSpeakerSettingsTests
{
    [Fact]
    public void OlderSettingsReadWithDefaults()
    {
        var fx = JsonSerializer.Deserialize<EffectsSettings>("""{"crossfeedEnabled": true, "crossfeed": 0.4}""", TaplineJson.Options);
        Assert.True(fx.CrossfeedEnabled);
        Assert.False(fx.VirtualSpeakersEnabled);
        Assert.Equal(EffectsSettings.DefaultVirtualSpeakers, fx.VirtualSpeakers);
    }

    [Fact]
    public void UsesTheMacKeys()
    {
        var json = JsonSerializer.Serialize(new EffectsSettings { VirtualSpeakersEnabled = true, VirtualSpeakers = "B0B5" }, TaplineJson.Options);
        Assert.Contains("\"virtualSpeakersEnabled\":true", json);
        Assert.Contains("\"virtualSpeakers\":\"B0B5\"", json);
        Assert.Equal("B0B5", JsonSerializer.Deserialize<EffectsSettings>(json, TaplineJson.Options).VirtualSpeakers);
    }
}
