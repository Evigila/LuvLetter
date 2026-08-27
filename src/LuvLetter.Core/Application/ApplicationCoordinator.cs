using Microsoft.Extensions.Hosting;
using LuvLetter.Core.Activation;
using LuvLetter.Core.Commands;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.NativeShell;
using LuvLetter.Core.Plugins;

namespace LuvLetter.Core.Application;

/// <summary>
/// Owns the application-level startup, event wiring and shutdown transaction.
/// </summary>
public sealed class ApplicationCoordinator : IHostedService
{
    private readonly ILuvLetterConfigurationStore configurationStore;
    private readonly CommandDispatcher commandDispatcher;
    private readonly QuickActionRegistry quickActions;
    private readonly IReadOnlyList<ILuvLetterPlugin> builtInPlugins;
    private readonly IReadOnlyList<IGeneralInputMatcher> generalInputMatchers;
    private readonly IActivationGestureService activationGestures;
    private readonly INativeShell nativeShell;
    private readonly IApplicationShell applicationShell;
    private PluginSession? pluginSession;
    private int started;
    private int stopping;
    private bool eventsSubscribed;

    public ApplicationCoordinator(
        ILuvLetterConfigurationStore configurationStore,
        CommandDispatcher commandDispatcher,
        QuickActionRegistry quickActions,
        IEnumerable<ILuvLetterPlugin> builtInPlugins,
        IEnumerable<IGeneralInputMatcher> generalInputMatchers,
        IActivationGestureService activationGestures,
        INativeShell nativeShell,
        IApplicationShell applicationShell)
    {
        this.configurationStore = configurationStore;
        this.commandDispatcher = commandDispatcher;
        this.quickActions = quickActions;
        this.builtInPlugins = builtInPlugins.ToArray();
        this.generalInputMatchers = generalInputMatchers.ToArray();
        this.activationGestures = activationGestures;
        this.nativeShell = nativeShell;
        this.applicationShell = applicationShell;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("The application coordinator has already started.");
        }

        var warnings = new List<string>();
        try
        {
            var configuration = configurationStore.Current;
            nativeShell.ApplyConfiguration(configuration.InputBox, configuration.QuickActions);

            cancellationToken.ThrowIfCancellationRequested();
            pluginSession = PluginLoader.Load(
                builtInPlugins,
                commandDispatcher,
                quickActions);
            warnings.AddRange(pluginSession.Warnings);

            try
            {
                nativeShell.SynchronizeQuickActions(quickActions.ItemSnapshot());
            }
            catch (Exception exception)
            {
                warnings.Add($"Cannot synchronize quick actions: {exception.Message}");
            }

            if (configurationStore.InitialLoad.HasWarning
                && !string.IsNullOrWhiteSpace(configurationStore.InitialLoad.Message))
            {
                warnings.Insert(0, configurationStore.InitialLoad.Message);
            }

            SubscribeEvents();

            try
            {
                activationGestures.Start(configuration.ActivationGestures);
            }
            catch (Exception exception)
            {
                TryStopActivationGestures();
                warnings.Add($"Cannot start Ctrl gestures: {exception.Message}");
                applicationShell.ShowSettings();
            }

            if (warnings.Count > 0)
            {
                ReportStatus(string.Join(" ", warnings));
            }

            return Task.CompletedTask;
        }
        catch
        {
            StopCore();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        StopCore();
        return Task.CompletedTask;
    }

    private void SubscribeEvents()
    {
        nativeShell.InputSubmitted += HandleInputSubmitted;
        nativeShell.QuickActionActivated += HandleQuickActionActivated;
        nativeShell.QuickActionUnavailable += HandleQuickActionUnavailable;
        quickActions.Changed += HandleQuickActionsChanged;
        commandDispatcher.Unhandled += HandleUnhandledCommand;
        commandDispatcher.Failed += HandleFailedCommand;
        activationGestures.CommandInputRequested += HandleCommandInputRequested;
        activationGestures.PopupsDismissRequested += HandlePopupsDismissRequested;
        activationGestures.QuickActionsRequested += HandleQuickActionsRequested;
        activationGestures.MessageQueueToggleRequested += HandleMessageQueueToggleRequested;
        eventsSubscribed = true;
    }

    private void UnsubscribeEvents()
    {
        if (!eventsSubscribed)
        {
            return;
        }

        activationGestures.MessageQueueToggleRequested -= HandleMessageQueueToggleRequested;
        activationGestures.QuickActionsRequested -= HandleQuickActionsRequested;
        activationGestures.CommandInputRequested -= HandleCommandInputRequested;
        activationGestures.PopupsDismissRequested -= HandlePopupsDismissRequested;
        commandDispatcher.Failed -= HandleFailedCommand;
        commandDispatcher.Unhandled -= HandleUnhandledCommand;
        quickActions.Changed -= HandleQuickActionsChanged;
        nativeShell.QuickActionActivated -= HandleQuickActionActivated;
        nativeShell.QuickActionUnavailable -= HandleQuickActionUnavailable;
        nativeShell.InputSubmitted -= HandleInputSubmitted;
        eventsSubscribed = false;
    }

    private void StopCore()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0)
        {
            return;
        }

        TryStopActivationGestures();
        UnsubscribeEvents();
        TryNativeAction(nativeShell.HideQuickActions, "hide quick actions");
        TryNativeAction(nativeShell.HideCommandInput, "hide the command input");

        try
        {
            pluginSession?.Dispose();
        }
        catch (Exception exception)
        {
            ReportStatus($"Cannot stop plugins: {exception.Message}");
        }
        finally
        {
            pluginSession = null;
        }
    }

    private void HandleInputSubmitted(InputSubmission submission)
    {
        if (Volatile.Read(ref stopping) != 0)
        {
            return;
        }

        switch (submission.Mode)
        {
            case InputMode.Ask:
                Echo(submission.Text);
                return;
            case InputMode.Command:
                DispatchCommand(submission.Text);
                return;
            case InputMode.General:
                HandleGeneralInput(submission.Text);
                return;
            default:
                return;
        }
    }

    private void HandleGeneralInput(string input)
    {
        if (commandDispatcher.IsRegisteredInvocation(input))
        {
            DispatchCommand(input);
            return;
        }

        foreach (var matcher in generalInputMatchers)
        {
            try
            {
                if (matcher.TryHandle(input))
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                ReportStatus($"General input matcher failed: {exception.Message}");
            }
        }

        Echo(input);
    }

    private void DispatchCommand(string commandText)
    {
        var result = commandDispatcher.Dispatch(commandText);
        if (result != CommandDispatchResult.Accepted)
        {
            ReportStatus($"Command was not accepted: {result}");
        }
    }

    private void Echo(string input) => ReportStatus($"Echo: {input}");

    private void HandleQuickActionActivated(string quickActionId)
    {
        if (Volatile.Read(ref stopping) == 0)
        {
            _ = ActivateQuickActionAsync(quickActionId);
        }
    }

    private void HandleQuickActionUnavailable()
    {
        if (Volatile.Read(ref stopping) == 0)
        {
            ReportStatus("No feature is assigned to that key.");
        }
    }

    private async Task ActivateQuickActionAsync(string quickActionId)
    {
        var result = await quickActions.ActivateAsync(quickActionId).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return;
        }

        var detail = result.Exception is null ? result.Status.ToString() : result.Exception.Message;
        ReportStatus(
            $"Cannot activate quick action '{quickActionId}': {detail}");
    }

    private void HandleQuickActionsChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        TryNativeAction(
            () => nativeShell.SynchronizeQuickActions(quickActions.ItemSnapshot()),
            "synchronize quick actions");
    }

    private void HandleUnhandledCommand(CommandInvocation invocation) =>
        ReportStatus($"Unknown command: {invocation.CommandName}");

    private void HandleFailedCommand(CommandInvocation invocation, Exception exception) =>
        ReportStatus(
            $"Command '{invocation.CommandName}' failed: {exception.Message}");

    private void HandleCommandInputRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        TryNativeAction(nativeShell.ToggleCommandInput, "toggle the command input");
    }

    private void HandlePopupsDismissRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        TryNativeAction(nativeShell.HidePopups, "hide popups");
    }

    private void HandleQuickActionsRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        TryNativeAction(nativeShell.ToggleQuickActions, "toggle quick actions");
    }

    private void HandleMessageQueueToggleRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        TryNativeAction(nativeShell.ToggleMessageQueue, "toggle the message queue");
    }

    private void TryStopActivationGestures()
    {
        try
        {
            activationGestures.Stop();
        }
        catch (Exception exception)
        {
            ReportStatus($"Cannot stop Ctrl gestures: {exception.Message}");
        }
    }

    private void TryNativeAction(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ReportStatus($"Cannot {operation}: {exception.Message}");
        }
    }

    private void ReportStatus(string message)
    {
        try
        {
            nativeShell.EnqueueMessage(message);
        }
        catch
        {
            // The existing application status surface remains the fallback if Native fails.
        }

        applicationShell.ReportStatus(message);
    }
}
