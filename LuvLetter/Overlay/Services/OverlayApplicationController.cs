using LuvLetter.Input;

namespace LuvLetter.Overlay.Services;

public sealed class OverlayApplicationController(
    INativeOverlayService nativeOverlayService,
    OverlayCliController cliController,
    GlobalKeyboardMonitor keyboardMonitor,
    OverlayCliInputHost cliInputHost
)
{
    private readonly INativeOverlayService nativeOverlayService = nativeOverlayService;
    private readonly OverlayCliController cliController = cliController;
    private readonly GlobalKeyboardMonitor keyboardMonitor = keyboardMonitor;
    private readonly OverlayCliInputHost cliInputHost = cliInputHost;

    public void Attach(App app, MainWindow mainWindow)
    {
        cliInputHost.Start();
        keyboardMonitor.Start();
        mainWindow.ContentRendered += StartOverlayAfterRender;

        app.Exit += (_, _) =>
        {
            cliInputHost.Stop();
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
