using System.Windows;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Core.Native;
using LuvLetter.Hotkeys;

namespace LuvLetter;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, "app.LuvLetter.Mooreforin", out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("LuvLetter is already running.", "LuvLetter");
            return;
        }

        var app = new App();
        var hotkeyStore = new HotkeyConfigurationStore();
        using var inputBoxService = new InputBoxService();
        using var hotkeyService = new GlobalHotkeyService(inputBoxService);
        var mainWindow = new MainWindow(hotkeyStore, hotkeyService);

        app.Exit += (_, _) => inputBoxService.Hide();

        try
        {
            hotkeyService.Start(hotkeyStore.Current);
        }
        catch (Exception exception)
        {
            mainWindow.SetStatus(exception.Message);
        }

        app.Run(mainWindow);
    }
}
