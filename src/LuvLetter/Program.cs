using LuvLetter.Core.Configuration;
using LuvLetter.Core.Native;
using LuvLetter.Hotkeys;
using LuvLetter.Tray;

namespace LuvLetter;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, "app.LuvLetter.AcksheedSys", out var isNewInstance);
        if (!isNewInstance)
        {
            System.Windows.MessageBox.Show("LuvLetter is already running.", "LuvLetter");
            return;
        }

        var app = new App();
        var configurationStore = new LuvLetterConfigurationStore();
        using var inputBoxService = new InputBoxService();
        inputBoxService.ApplyConfiguration(configurationStore.Current.InputBox);
        using var hotkeyService = new GlobalHotkeyService(inputBoxService);
        var mainWindow = new MainWindow(configurationStore, hotkeyService, inputBoxService);
        using var trayIconService = new TrayIconService(app, mainWindow);

        app.Exit += (_, _) => inputBoxService.Hide();

        try
        {
            hotkeyService.Start(configurationStore.Current.InputBox.Hotkeys.Activation);
        }
        catch (Exception exception)
        {
            mainWindow.SetStatus(exception.Message);
        }

        app.MainWindow = mainWindow;
        trayIconService.StartMinimized();
        app.Run();
    }
}
