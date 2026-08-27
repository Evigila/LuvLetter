using LuvLetter.Core.Application;
using LuvLetter.Core.Commands;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.Modules.Settings;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static async Task TestApplicationCoordinatorLifecycle()
    {
        using var commands = new CommandDispatcher();
        var quickActions = new QuickActionRegistry();
        var activation = new FakeActivationGestureService();
        var nativeShell = new FakeNativeShell();
        var applicationShell = new FakeApplicationShell();
        var coordinator = new ApplicationCoordinator(
            new FakeConfigurationStore(LuvLetterConfiguration.Default),
            commands,
            quickActions,
            [new SettingsPlugin(applicationShell)],
            activation,
            nativeShell,
            applicationShell);

        await coordinator.StartAsync(CancellationToken.None);
        Assert.Equal(1, nativeShell.AppliedConfigurations);
        Assert.Equal(1, nativeShell.SynchronizedSnapshots.Count);
        Assert.Equal("settings.open", nativeShell.SynchronizedSnapshots[0].Single().Id);
        Assert.Equal("Control Center", nativeShell.SynchronizedSnapshots[0].Single().DisplayName);
        Assert.Equal(1, activation.AppliedOptions.Count);
        Assert.Equal(0, applicationShell.ShowSettingsCalls);

        activation.RaiseCommandInputRequested();
        activation.RaiseQuickActionsRequested();
        activation.RaiseMessageQueueToggleRequested();
        activation.RaisePopupsDismissRequested();
        Assert.Equal(1, nativeShell.ToggleCommandInputCalls);
        Assert.Equal(1, nativeShell.ToggleQuickActionsCalls);
        Assert.Equal(1, nativeShell.ToggleMessageQueueCalls);
        Assert.Equal(1, nativeShell.HidePopupsCalls);
        Assert.Equal(0, nativeShell.HideCommandInputCalls);
        Assert.Equal(0, nativeShell.HideQuickActionsCalls);

        nativeShell.RaiseQuickActionActivated("settings.open");
        await Task.Delay(25);
        Assert.Equal(1, applicationShell.ShowSettingsCalls);

        nativeShell.RaiseQuickActionUnavailable();
        Assert.SequenceEqual(
            ["No feature is assigned to that key."],
            nativeShell.EnqueuedMessages,
            "An unavailable Quick Action must be forwarded to the Native message queue.");
        Assert.SequenceEqual(
            ["No feature is assigned to that key."],
            applicationShell.Statuses,
            "The existing status surface must remain synchronized with queue messages.");

        await coordinator.StopAsync(CancellationToken.None);
        await coordinator.StopAsync(CancellationToken.None);
        Assert.Equal(1, activation.StopCalls);
        Assert.Equal(1, nativeShell.HideCommandInputCalls);
        Assert.Equal(1, nativeShell.HideQuickActionsCalls);

        activation.RaiseQuickActionsRequested();
        Assert.Equal(
            1,
            nativeShell.ToggleQuickActionsCalls,
            "Application coordinator shutdown left an activation event subscribed.");
        activation.RaisePopupsDismissRequested();
        Assert.Equal(
            1,
            nativeShell.HidePopupsCalls,
            "Application coordinator shutdown left a dismissal event subscribed.");
        activation.RaiseMessageQueueToggleRequested();
        Assert.Equal(
            1,
            nativeShell.ToggleMessageQueueCalls,
            "Application coordinator shutdown left a message-queue event subscribed.");
        nativeShell.RaiseQuickActionUnavailable();
        Assert.Equal(
            1,
            nativeShell.EnqueuedMessages.Count,
            "Application coordinator shutdown left an unavailable-action event subscribed.");
    }
}
