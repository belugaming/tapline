using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Tapline.Infrastructure;

namespace Tapline.UI;

/// A window with Tapline's dark chrome.
class DarkWindow : Window
{
    public DarkWindow()
    {
        Background = Theme.WindowBackground;
        Foreground = Theme.Text;
        FontFamily = Theme.UiFont;
        FontSize = 13;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }
}

static class Dialogs
{
    /// Asks for a preset name. With `offerEffects`, also whether to include the effects.
    public static (bool Ok, string Name, bool IncludeEffects) SavePreset(Window? owner, string name, bool offerEffects, string? title = null)
    {
        var window = new DarkWindow
        {
            Title = title ?? L.T("Save Preset"),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Owner = owner,
        };
        var box = new TextBox { Text = name, Width = 300 };
        var effects = new CheckBox
        {
            Content = Ui.VStack(1, Ui.Text(L.T("Include effects")), Ui.Caption(L.T("Crossfeed, balance and width switch together with the curve."))),
            Visibility = offerEffects ? Visibility.Visible : Visibility.Collapsed,
            Width = 300,
        };
        var cancel = Ui.Button(L.T("Cancel"), () => window.DialogResult = false);
        cancel.IsCancel = true;
        var save = Ui.Button(L.T("Save"), () =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text)) window.DialogResult = true;
        }, accent: true);
        save.IsDefault = true;
        box.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(box.Text);
        save.IsEnabled = !string.IsNullOrWhiteSpace(name);
        window.Content = Ui.VStack(14,
            Ui.Headline(title ?? L.T("Save Preset")),
            Ui.VStack(4, Ui.Caption(L.T("Preset name")), box),
            effects,
            Ui.HStack(8, Ui.Spacer(), cancel, save)).Margin(20, 18, 20, 18);
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        var ok = window.ShowDialog() == true;
        return (ok, box.Text.Trim(), effects.IsChecked == true);
    }

    public static string? OpenFile(string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static IReadOnlyList<string> OpenFiles(string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, Multiselect = true };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public static string? SaveFile(string filter, string fileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = fileName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// Reads a text file as UTF-8, falling back to Latin-1 (REW and some measurement rigs).
    public static string? ReadText(string path)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            try { return new System.Text.UTF8Encoding(false, true).GetString(bytes); }
            catch (System.Text.DecoderFallbackException) { return System.Text.Encoding.Latin1.GetString(bytes); }
        }
        catch
        {
            return null;
        }
    }
}
