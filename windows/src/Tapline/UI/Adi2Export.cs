using System.IO;
using System.Text;
using Tapline.Core;
using Tapline.Infrastructure;

namespace Tapline.UI;

/// Saving RME ADI-2 presets, shared by the room page and Import & Export.
static class Adi2Export
{
    /// What to do with a saved .adieqpr file.
    public static string LoadHint => L.T("In ADI-2 Remote, open EQ Presets, click File – Load and pick the file, then Store it to the unit.");

    /// Asks where to save and writes the file. Returns false when cancelled.
    public static bool Save(string text, string suggestedName)
    {
        var ext = Adi2Preset.FileExtension;
        var path = Dialogs.SaveFile($"{L.T("RME ADI-2 EQ preset")} (*.{ext})|*.{ext}", SafeFileName(suggestedName) + "." + ext);
        if (path == null) return false;
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return true;
    }

    /// A user-facing message for a failed ADI-2 export.
    public static string Message(Exception error) => error is Adi2PresetException adi2
        ? adi2.Error switch
        {
            Adi2PresetError.DoesNotFit => L.T("The ADI-2 has five bells per channel, and bands 4 and 5 only work from 200 Hz up with Q up to 5. Use fewer bands, or fit the room correction for the ADI-2."),
            Adi2PresetError.UnsupportedFilter => L.T("The ADI-2 takes bells, one low shelf (as Bass, 20–150 Hz) and one high shelf (as Treble, 3–10 kHz). Change or remove the other filters."),
            _ => L.T("That is not an ADI-2 EQ preset."),
        }
        : error.Message;

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "Tapline" : safe;
    }
}
