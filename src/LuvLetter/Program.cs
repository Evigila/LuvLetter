using System.Windows;
using LuvLetter.Core.Activation;
using LuvLetter.Core.Application;
using LuvLetter.Core.Commands;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Features;
using LuvLetter.Core.Modules;
using LuvLetter.Core.Native;
using LuvLetter.Hotkeys;
using LuvLetter.Modules;
using LuvLetter.Tray;
using WpfMessageBox = System.Windows.MessageBox;

namespace LuvLetter;

internal static class Program
{
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
        var configurationApplicationService = new ConfigurationApplicationService(
            configurationStore,
            hotkeyService,
            inputBoxService);
        var featureRegistry = new FeatureRegistry();
        using var trayIconService = new TrayIconService(
            app,
            () => new MainWindow(configurationApplicationService));
        var startupWarnings = new List<string>();
        ModuleRegistrationResult? moduleRegistration = null;
        if (configurationStore.InitialLoad.HasWarning
            && !string.IsNullOrWhiteSpace(configurationStore.InitialLoad.Message))
        {
            startupWarnings.Add(configurationStore.InitialLoad.Message);
        }

        try
        {
            var moduleDiscovery = ModuleCatalog.Discover([new BuiltInModule()]);
            moduleRegistration = ModuleRegistrar.Register(
                moduleDiscovery.Modules,
                commandDispatcher,
                featureRegistry,
                trayIconService.ShowWindow);

            if (moduleDiscovery.Warnings.Count > 0)
            {
                startupWarnings.AddRange(moduleDiscovery.Warnings);
            }

            if (moduleRegistration.Warnings.Count > 0)
            {
                startupWarnings.AddRange(moduleRegistration.Warnings);
            }
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(
                $"Cannot register LuvLetter modules.\n\n{exception.Message}",
                "LuvLetter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        using var moduleRegistrationLifetime = moduleRegistration;

        try
        {
            inputBoxService.SynchronizeFeatures(featureRegistry.ItemSnapshot());
        }
        catch (Exception exception)
        {
            startupWarnings.Add($"Cannot register features: {exception.Message}");
        }

        if (startupWarnings.Count > 0)
        {
            trayIconService.SetStatus(string.Join(" ", startupWarnings));
        }

        inputBoxService.InputSubmitted += HandleInputSubmitted;
        inputBoxService.FeatureActivated += HandleFeatureActivated;
        featureRegistry.Changed += HandleFeaturesChanged;
        commandDispatcher.Unhandled += HandleUnhandledCommand;
        commandDispatcher.Failed += HandleFailedCommand;
        hotkeyService.CommandRequested += HandleCommandRequested;
        hotkeyService.FeatureWindowRequested += HandleFeatureWindowRequested;

        app.Exit += HandleApplicationExit;

        var activationReady = true;
        try
        {
            hotkeyService.Start(configurationStore.Current.ActivationGestures);
        }
        catch (Exception exception)
        {
            activationReady = false;
            trayIconService.SetStatus($"Cannot start Ctrl gestures: {exception.Message}");
        }

        if (activationReady)
        {
            trayIconService.StartMinimized();
        }
        else
        {
            trayIconService.ShowWindow();
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
            _ = ActivateFeatureAsync(featureId);
        }

        async Task ActivateFeatureAsync(string featureId)
        {
            var result = await featureRegistry.ActivateAsync(featureId).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return;
            }

            var detail = result.Exception is null ? result.Status.ToString() : result.Exception.Message;
            PostStatus($"Cannot activate feature '{featureId}': {detail}");
        }

        void HandleFeaturesChanged(object? sender, EventArgs eventArgs)
        {
            try
            {
                inputBoxService.SynchronizeFeatures(featureRegistry.ItemSnapshot());
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
                trayIconService.SetStatus($"Cannot {operation}: {exception.Message}");
            }
        }

        void PostStatus(string status)
        {
            if (!app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
            {
                trayIconService.SetStatus(status);
            }
        }
    }
}
