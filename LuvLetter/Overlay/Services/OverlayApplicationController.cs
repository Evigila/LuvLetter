using LuvLetter.Input;

namespace LuvLetter.Overlay.Services;

public sealed class OverlayApplicationController(
    INativeOverlayService nativeOverlayService,
    OverlayCliController cliController,
    GlobalKeyboardMonitor keyboardMonitor
)
{
    private readonly INativeOverlayService nativeOverlayService = nativeOverlayService;
    private readonly OverlayCliController cliController = cliController;
    private readonly GlobalKeyboardMonitor keyboardMonitor = keyboardMonitor;

    public void Attach(App app, MainWindow mainWindow)
    {
        keyboardMonitor.Start();
        mainWindow.ContentRendered += StartOverlayAfterRender;

        app.Exit += (_, _) =>
        {
            keyboardMonitor.Stop();
            nativeOverlayService.Stop();
        };
    }

    private void StartOverlayAfterRender(object? sender, EventArgs eventArgs)
    {
        if (sender is not MainWindow mainWindow)
        {
            return;
        }

        mainWindow.ContentRendered -= StartOverlayAfterRender;

        _ = StartOverlayAsync();
    }

    private async Task StartOverlayAsync()
    {
        try
        {
            await nativeOverlayService.StartAsync();
            cliController.ApplyStateToOverlay();
        }
        catch (Exception exception)
        {
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                System.Windows.MessageBox.Show(exception.Message, "Error")
            );
        }
    }
}
