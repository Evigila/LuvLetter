using System.Windows;
using LuvLetter.Core.Commands;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Features;
using LuvLetter.Core.Native;
using LuvLetter.Hotkeys;
using LuvLetter.Tray;
using WpfMessageBox = System.Windows.MessageBox;

namespace LuvLetter;

internal static class Program
{
    private const int TestFeatureCount = 9;

    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, "app.LuvLetter.AcksheedSys", out var isNewInstance);
        if (!isNewInstance)
        {
            WpfMessageBox.Show("LuvLetter is already running.", "LuvLetter");
            return;
        }

        var app = new App();
        LuvLetterConfigurationStore configurationStore;
        try
        {
            configurationStore = new LuvLetterConfigurationStore();
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(
                $"Cannot load LuvLetter settings.\n\n{exception.Message}",
                "LuvLetter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        using var commandDispatcher = new CommandDispatcher();

        InputBoxService? nativeService = null;
        try
        {
            nativeService = new InputBoxService();
            nativeService.ApplyConfiguration(
                configurationStore.Current.InputBox,
                configurationStore.Current.FeatureWindow);
        }
        catch (Exception exception)
        {
            nativeService?.Dispose();
            WpfMessageBox.Show(
                $"Cannot initialize the native LuvLetter shell.\n\n{exception.Message}",
                "LuvLetter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        using var inputBoxService = nativeService!;
        using var hotkeyService = new GlobalHotkeyService();
        var featureRegistry = new FeatureRegistry();
        var mainWindow = new MainWindow(configurationStore, hotkeyService, inputBoxService);
        RegisterTestFeatures(featureRegistry);

        try
        {
            inputBoxService.SynchronizeFeatures(featureRegistry.Snapshot());
        }
        catch (Exception exception)
        {
            mainWindow.SetStatus($"Cannot register test features: {exception.Message}");
        }

        inputBoxService.InputSubmitted += HandleInputSubmitted;
        inputBoxService.FeatureActivated += HandleFeatureActivated;
        featureRegistry.Changed += HandleFeaturesChanged;
        commandDispatcher.Unhandled += HandleUnhandledCommand;
        commandDispatcher.Failed += HandleFailedCommand;
        hotkeyService.CommandRequested += HandleCommandRequested;
        hotkeyService.FeatureWindowRequested += HandleFeatureWindowRequested;

        using var trayIconService = new TrayIconService(app, mainWindow);
        app.Exit += HandleApplicationExit;

        var activationReady = true;
        try
        {
            hotkeyService.Start(configurationStore.Current.ActivationGestures);
        }
        catch (Exception exception)
        {
            activationReady = false;
            mainWindow.SetStatus($"Cannot start Ctrl gestures: {exception.Message}");
        }

        app.MainWindow = mainWindow;
        if (activationReady)
        {
            trayIconService.StartMinimized();
        }
        else
        {
            mainWindow.Show();
            mainWindow.Activate();
        }

        app.Run();

        app.Exit -= HandleApplicationExit;
        hotkeyService.FeatureWindowRequested -= HandleFeatureWindowRequested;
        hotkeyService.CommandRequested -= HandleCommandRequested;
        commandDispatcher.Failed -= HandleFailedCommand;
        commandDispatcher.Unhandled -= HandleUnhandledCommand;
        featureRegistry.Changed -= HandleFeaturesChanged;
        inputBoxService.FeatureActivated -= HandleFeatureActivated;
        inputBoxService.InputSubmitted -= HandleInputSubmitted;
        return;

        void HandleInputSubmitted(string commandText)
        {
            var result = commandDispatcher.Dispatch(commandText);
            if (result != CommandDispatchResult.Accepted)
            {
                PostStatus($"Command was not accepted: {result}");
            }
        }

        void HandleFeatureActivated(string featureId)
        {
            _ = app.Dispatcher.BeginInvoke(() =>
            {
                if (!featureRegistry.TryActivate(featureId))
                {
                    mainWindow.SetStatus($"Cannot activate feature '{featureId}'.");
                }
            });
        }

        void HandleFeaturesChanged(object? sender, EventArgs eventArgs)
        {
            try
            {
                inputBoxService.SynchronizeFeatures(featureRegistry.Snapshot());
            }
            catch (Exception exception)
            {
                PostStatus($"Cannot synchronize features: {exception.Message}");
            }
        }

        void HandleUnhandledCommand(object? sender, CommandInvocationEventArgs eventArgs)
        {
            PostStatus($"Unknown command: {eventArgs.Invocation.CommandName}");
        }

        void HandleFailedCommand(object? sender, CommandDispatchFailedEventArgs eventArgs)
        {
            PostStatus(
                $"Command '{eventArgs.Invocation.CommandName}' failed: {eventArgs.Exception.Message}");
        }

        void HandleCommandRequested(object? sender, EventArgs eventArgs)
        {
            TryNativeAction(inputBoxService.Toggle, "toggle the command input");
        }

        void HandleFeatureWindowRequested(object? sender, EventArgs eventArgs)
        {
            TryNativeAction(inputBoxService.ToggleFeatureWindow, "toggle the feature window");
        }

        void HandleApplicationExit(object? sender, ExitEventArgs eventArgs)
        {
            try
            {
                inputBoxService.HideFeatureWindow();
                inputBoxService.Hide();
            }
            catch
            {
                // Dispose performs the final bounded native shutdown.
            }
        }

        void TryNativeAction(Action action, string operation)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                mainWindow.SetStatus($"Cannot {operation}: {exception.Message}");
            }
        }

        void PostStatus(string status)
        {
            if (!app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            {
                _ = app.Dispatcher.BeginInvoke(() => mainWindow.SetStatus(status));
            }
        }
    }

    private static void RegisterTestFeatures(FeatureRegistry registry)
    {
        for (var index = 1; index <= TestFeatureCount; index++)
        {
            var featureNumber = index;
            _ = registry.Register(
                new FeatureDefinition(
                    $"test.feature.{featureNumber}",
                    $"测试功能 {featureNumber}",
                    () => WpfMessageBox.Show(
                        $"已激活测试功能 {featureNumber}。",
                        "LuvLetter",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information)));
        }
    }
}
