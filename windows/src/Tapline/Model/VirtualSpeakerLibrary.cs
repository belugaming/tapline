using System.IO;
using System.Reflection;
using System.Text.Json.Serialization;
using Tapline.Core;
using Tapline.Infrastructure;

namespace Tapline.Model;

/// A set of impulse responses for Virtual Speakers: built into the app or imported.
sealed record VirtualSpeakerSet(string Id, string Name, BinauralLayout Layout, double SampleRate, int Length, bool IsBuiltIn = false)
{
    [JsonIgnore] public double Duration => SampleRate > 0 ? Length / SampleRate : 0;
}

/// The sets to choose from (as VirtualSpeakerLibrary.swift). The built-in sets are embedded in
/// the app; imported ones are stored as 4-channel float WAVs in %APPDATA%\Tapline\VirtualSpeakers
/// and listed in the state file.
sealed class VirtualSpeakerLibrary(List<VirtualSpeakerSet> imported, Action changed)
{
    public static readonly IReadOnlyList<VirtualSpeakerSet> BuiltIn =
    [
        new(EffectsSettings.DefaultVirtualSpeakers, L.T("Studio room (KU100)"), BinauralLayout.FourChannel, 48000, 0, IsBuiltIn: true),
        new("sadie-ku100", L.T("Anechoic (KU100)"), BinauralLayout.FourChannel, 48000, 0, IsBuiltIn: true),
    ];

    private static readonly Dictionary<string, string> Resources = new()
    {
        [EffectsSettings.DefaultVirtualSpeakers] = "SADIE-II-KU100-Room.wav",
        ["sadie-ku100"] = "SADIE-II-KU100.wav",
    };

    private readonly Dictionary<string, BinauralIrs> loaded = [];

    public IReadOnlyList<VirtualSpeakerSet> Imported => imported;
    public IReadOnlyList<VirtualSpeakerSet> All => [.. BuiltIn, .. imported];

    /// The set an id from the settings stands for; a removed import falls back to the default.
    public VirtualSpeakerSet Resolve(string id) => All.FirstOrDefault(s => s.Id == id) ?? BuiltIn[0];

    public BinauralIrs Load(VirtualSpeakerSet set)
    {
        if (loaded.TryGetValue(set.Id, out var irs)) return irs;
        byte[] data;
        if (Resources.TryGetValue(set.Id, out var resource))
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException("Missing built-in impulse responses.", resource);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            data = memory.ToArray();
        }
        else
        {
            data = File.ReadAllBytes(Path.Combine(Folder, set.Id + ".wav"));
        }
        irs = BinauralIrs.FromWav(WavFile.Parse(data)).Trimmed();
        loaded[set.Id] = irs;
        return irs;
    }

    /// Reads one file (HeSuVi, Impulcifer, 4 channels, one stereo speaker) or two stereo files
    /// (one per speaker) and adds them as a new set.
    public VirtualSpeakerSet Import(IReadOnlyList<string> paths)
    {
        var files = paths.Select(p => WavFile.Parse(File.ReadAllBytes(p))).ToList();
        var irs = BinauralIrs.FromFiles(files).Trimmed();
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        var set = new VirtualSpeakerSet(id, NameFor(paths), irs.Layout, irs.SampleRate, irs.Length);
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, id + ".wav"), irs.ToWav().Encode());
        loaded[id] = irs;
        imported.Add(set);
        changed();
        return set;
    }

    public void Remove(string id)
    {
        var index = imported.FindIndex(s => s.Id == id);
        if (index < 0) return;
        imported.RemoveAt(index);
        loaded.Remove(id);
        try { File.Delete(Path.Combine(Folder, id + ".wav")); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        changed();
    }

    private static string Folder => Path.Combine(AppPaths.Data, "VirtualSpeakers");

    /// The file name, or for two files their common start ("Room L" + "Room R" → "Room").
    private static string NameFor(IReadOnlyList<string> paths)
    {
        var names = paths.Select(p => Path.GetFileNameWithoutExtension(p) ?? "").ToList();
        if (names.Count != 2) return names.FirstOrDefault() is { Length: > 0 } n ? n : L.T("Imported speakers");
        var common = new string(names[0].Zip(names[1]).TakeWhile(p => p.First == p.Second).Select(p => p.First).ToArray())
            .TrimEnd(' ', '-', '_', '.', '(');
        return common.Length >= 3 ? common : names[0];
    }

    /// What went wrong with an import or a load, for the user.
    public static string Message(Exception error) => error switch
    {
        BinauralImportException { Error: BinauralImportError.UnsupportedChannelCount } e =>
            L.F("That file has %lld channels. Tapline reads HeSuVi files (7 or 14 channels), Impulcifer's hrir.wav (16), 4-channel files, or stereo files of one speaker each.", e.Count),
        BinauralImportException { Error: BinauralImportError.Silent } => L.T("The front speakers in that file are silent."),
        BinauralImportException { Error: BinauralImportError.SameSide } => L.T("Both files are the same speaker. Choose one file for the left speaker and one for the right."),
        BinauralImportException { Error: BinauralImportError.SampleRateMismatch } => L.T("The two files have different sample rates."),
        BinauralImportException { Error: BinauralImportError.WrongFileCount } => L.T("Choose one file, or two stereo files (one per speaker)."),
        WavFormatException => L.T("That is not a WAV file Tapline can read (PCM or 32/64-bit float)."),
        _ => error.Message,
    };
}
