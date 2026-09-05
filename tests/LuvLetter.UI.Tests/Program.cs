using LuvLetter.Core.Configuration;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Core.Modules.Settings;
using LuvLetter.View.Settings;

namespace LuvLetter.UI.Tests;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        try
        {
            var window = new SettingsWindow(new StubSettingsService());
            if (!string.Equals(
                    SurfaceStyleDefaults.FontFamily,
                    window.FontFamily.Source,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected Control Center font family: {window.FontFamily.Source}");
            }

            if (Math.Abs(window.FontSize - SurfaceStyleDefaults.FontSize) > double.Epsilon)
            {
                throw new InvalidOperationException(
                    $"Unexpected Control Center font size: {window.FontSize}");
            }

            window.Close();
            Console.WriteLine("PASS  Control Center XAML construction and typography");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL  Control Center XAML construction and typography");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public LuvLetterConfiguration Current => LuvLetterConfiguration.Default;

        public LuvLetterConfiguration CreateDefaultConfiguration() =>
            LuvLetterConfiguration.Default;

        public void CancelPendingGestures()
        {
        }

        public ConfigurationApplicationResult Apply(LuvLetterConfiguration configuration) =>
            throw new NotSupportedException();

        public bool TryMap(
            LuvLetterConfiguration baseline,
            SettingsEditorInput input,
            out LuvLetterConfiguration configuration,
            out string error)
        {
            configuration = baseline;
            error = string.Empty;
            return true;
        }

        public LuvLetterConfiguration ReplaceHotkey(
            LuvLetterConfiguration configuration,
            SettingsHotkeyField field,
            HotkeyDefinition hotkey) => configuration;
    }
}
