using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Tapline.Audio;

/// Listens to what the output device plays (WASAPI loopback, after Equalizer APO) for the
/// level meters and the spectrum analyzer. Nothing is stored or sent anywhere.
sealed class LoopbackMonitor : IDisposable
{
    public const int RingSize = 32768;

    private readonly object gate = new();
    private readonly float[] ring = new float[RingSize];
    private long written;
    private float peakL, peakR;
    private double sumL, sumR;
    private int count;
    private uint clipped;
    private WasapiLoopbackCapture? capture;
    private string? deviceId;

    public double SampleRate { get; private set; } = 48000;
    public bool IsRunning => capture != null;

    /// Starts listening to `device` (or restarts if it changed). Null stops.
    public void Follow(MMDevice? device)
    {
        if (device?.ID == deviceId && capture != null) return;
        Stop();
        if (device == null) return;
        try
        {
            var c = new WasapiLoopbackCapture(device);
            SampleRate = c.WaveFormat.SampleRate;
            var format = c.WaveFormat;
            c.DataAvailable += (_, e) => Ingest(e.Buffer, e.BytesRecorded, format);
            c.RecordingStopped += (_, _) => { };
            c.StartRecording();
            capture = c;
            deviceId = device.ID;
        }
        catch
        {
            capture = null;
            deviceId = null;
        }
    }

    public void Stop()
    {
        var c = capture;
        capture = null;
        deviceId = null;
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }

    private void Ingest(byte[] buffer, int bytes, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var isFloat = IsFloat(format);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample == 0) return;
        var frames = bytes / (bytesPerSample * channels);
        lock (gate)
        {
            for (var i = 0; i < frames; i++)
            {
                var offset = i * channels * bytesPerSample;
                var l = Read(buffer, offset, bytesPerSample, isFloat);
                var r = channels > 1 ? Read(buffer, offset + bytesPerSample, bytesPerSample, isFloat) : l;
                var al = Math.Abs(l);
                var ar = Math.Abs(r);
                if (al > peakL) peakL = al;
                if (ar > peakR) peakR = ar;
                if (al >= 1 || ar >= 1) clipped++;
                sumL += l * l;
                sumR += r * r;
                ring[written++ & (RingSize - 1)] = 0.5f * (l + r);
            }
            count += frames;
        }
    }

    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat
        || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);

    public static float Read(byte[] b, int offset, int size, bool isFloat)
    {
        if (isFloat && size == 4) return BitConverter.ToSingle(b, offset);
        return size switch
        {
            2 => BitConverter.ToInt16(b, offset) / 32768f,
            3 => ((b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16)) << 8 >> 8) / 8388608f,
            4 => BitConverter.ToInt32(b, offset) / 2147483648f,
            _ => 0,
        };
    }

    /// Peak and RMS of each channel since the last call, and how many samples clipped.
    public (float PeakL, float PeakR, float RmsL, float RmsR, uint Clipped) TakeMeters()
    {
        lock (gate)
        {
            var result = (peakL, peakR,
                          count > 0 ? (float)Math.Sqrt(sumL / count) : 0f,
                          count > 0 ? (float)Math.Sqrt(sumR / count) : 0f,
                          clipped);
            peakL = peakR = 0;
            sumL = sumR = 0;
            count = 0;
            clipped = 0;
            return result;
        }
    }

    /// Copies the most recent `destination.Length` mono samples. Returns the total written so far.
    public long CopyRecent(float[] destination)
    {
        lock (gate)
        {
            var n = Math.Min(destination.Length, RingSize);
            var start = written - n;
            for (var i = 0; i < n; i++)
                destination[i] = start + i >= 0 ? ring[(start + i) & (RingSize - 1)] : 0;
            return written;
        }
    }

    public void Dispose() => Stop();
}
