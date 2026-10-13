namespace Tapline.Core;

/// <summary>The impulse-response files Virtual Speakers need in Equalizer APO's config folder:
/// one mono 32-bit float WAV per speaker-to-ear path, at the device's sample rate (Equalizer APO's
/// convolver does not resample), with the loudness-matching gain applied.</summary>
public static class VirtualSpeakerFiles
{
    /// <summary>Subfolder of Equalizer APO's config folder, as written in the configuration.</summary>
    public const string Folder = @"Tapline\VirtualSpeakers";

    private static readonly string[] Paths = ["FL-L", "FL-R", "FR-L", "FR-R"];

    public sealed record IrFile(string Name, byte[] Data);

    /// <summary>File name stem for one set at one rate; ids are built-in names or GUIDs.</summary>
    public static string Stem(string setId, double sampleRate)
    {
        var safe = new string(setId.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        return $"{safe}-{(int)Math.Round(sampleRate)}";
    }

    /// <summary>The four files, in the order FL→L, FL→R, FR→L, FR→R.</summary>
    public static IReadOnlyList<IrFile> Build(BinauralIrs irs, string setId, double sampleRate)
    {
        var prepared = irs.Prepared(sampleRate);
        var gain = (float)prepared.LoudnessGain;
        var stem = Stem(setId, sampleRate);
        return Paths.Select((path, i) =>
        {
            var samples = prepared.Responses[i].Select(v => v * gain).ToArray();
            return new IrFile($"{stem}-{path}.wav", new WavFile(sampleRate, [samples]).Encode());
        }).ToList();
    }

    /// <summary>What the configuration refers to: the files' paths relative to config.txt.</summary>
    public static ApoConfig.SpeakerFiles Reference(IReadOnlyList<IrFile> files) =>
        new(Relative(files[0]), Relative(files[1]), Relative(files[2]), Relative(files[3]));

    private static string Relative(IrFile f) => Folder + @"\" + f.Name;
}
