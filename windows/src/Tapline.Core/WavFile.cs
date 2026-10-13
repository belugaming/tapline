using System.Buffers.Binary;
using System.Text;

namespace Tapline.Core;

public enum WavError { NotWav, MissingChunk, UnsupportedFormat, InvalidHeader, Empty }

public sealed class WavFormatException(WavError error, string message) : Exception(message)
{
    public WavError Error { get; } = error;
}

/// <summary>Audio from a RIFF WAVE file, as floating-point samples per channel (port of WAVFile.swift).</summary>
public sealed class WavFile(double sampleRate, float[][] channels)
{
    public double SampleRate { get; } = sampleRate;
    public float[][] Channels { get; } = channels;
    public int FrameCount => Channels.Length > 0 ? Channels[0].Length : 0;

    /// <summary>Reads PCM (8, 16, 24 and 32 bit), 32 and 64-bit float and WAVE_FORMAT_EXTENSIBLE files.
    /// A data chunk that runs past the end of the file is read as far as it goes.</summary>
    public static WavFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
            throw new WavFormatException(WavError.NotWav, "Not a WAV file.");
        (int Tag, int Channels, int Rate, int Align, int Bits)? format = null;
        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data.Slice(pos, 4));
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));
            var body = pos + 8;
            if (id == "fmt ")
            {
                if (size < 16 || body + 16 > data.Length) throw new WavFormatException(WavError.InvalidHeader, "Broken format chunk.");
                int tag = U16(data, body);
                // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                if (tag == 0xFFFE)
                {
                    if (size < 40 || body + 26 > data.Length) throw new WavFormatException(WavError.InvalidHeader, "Broken extensible format chunk.");
                    tag = U16(data, body + 24);
                }
                format = (tag, U16(data, body + 2), (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(body + 4, 4))),
                          U16(data, body + 12), U16(data, body + 14));
            }
            else if (id == "data")
            {
                if (format is not { } f) throw new WavFormatException(WavError.MissingChunk, "No format chunk before the data.");
                return Decode(data, body, (int)Math.Min(size, data.Length - body), f);
            }
            // Chunks are padded to an even size.
            var next = body + size + (size & 1);
            if (next > int.MaxValue) break;
            pos = (int)next;
        }
        throw new WavFormatException(WavError.MissingChunk, format == null ? "No format chunk." : "No data chunk.");
    }

    private static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o, 2));

    private static WavFile Decode(ReadOnlySpan<byte> data, int start, int count, (int Tag, int Channels, int Rate, int Align, int Bits) f)
    {
        var width = f.Bits / 8;
        var supported = f.Tag switch
        {
            1 => f.Bits is 8 or 16 or 24 or 32,
            3 => f.Bits is 32 or 64,
            _ => false,
        };
        if (!supported) throw new WavFormatException(WavError.UnsupportedFormat, $"Unsupported sample format (tag {f.Tag}, {f.Bits} bit).");
        if (f.Channels <= 0 || f.Rate <= 0 || f.Align < width * f.Channels) throw new WavFormatException(WavError.InvalidHeader, "Broken format chunk.");
        var frames = count / f.Align;
        if (frames <= 0) throw new WavFormatException(WavError.Empty, "The file has no samples.");

        var channels = new float[f.Channels][];
        for (var c = 0; c < f.Channels; c++) channels[c] = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < f.Channels; c++)
            {
                var s = data.Slice(start + i * f.Align + c * width, width);
                float v = (f.Tag, f.Bits) switch
                {
                    (1, 8) => (s[0] - 128) / 128f,
                    (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    (1, 24) => ((s[0] << 8 | s[1] << 16 | s[2] << 24) >> 8) / 8_388_608f,
                    (1, 32) => (float)(BinaryPrimitives.ReadInt32LittleEndian(s) / 2_147_483_648.0),
                    (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                    _ => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
                };
                channels[c][i] = float.IsFinite(v) ? v : 0;
            }
        }
        return new WavFile(f.Rate, channels);
    }

    /// <summary>32-bit float WAV.</summary>
    public byte[] Encode()
    {
        var count = Channels.Length;
        var frames = FrameCount;
        var dataSize = frames * count * 4;
        var rate = (int)Math.Round(SampleRate);
        var output = new byte[44 + dataSize];
        var o = output.AsSpan();
        "RIFF"u8.CopyTo(o);
        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], (uint)(36 + dataSize));
        "WAVEfmt "u8.CopyTo(o[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(o[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(o[20..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(o[22..], (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(o[24..], (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(o[28..], (uint)(rate * count * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(o[32..], (ushort)(count * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(o[34..], 32);
        "data"u8.CopyTo(o[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(o[40..], (uint)dataSize);
        var p = 44;
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < count; c++, p += 4)
                BinaryPrimitives.WriteSingleLittleEndian(o[p..], Channels[c][i]);
        return output;
    }
}
