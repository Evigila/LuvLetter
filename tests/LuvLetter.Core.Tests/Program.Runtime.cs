using LuvLetter.Core.Commands;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.Modules.Settings;
using LuvLetter.Core.Runtime;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static async Task TestHostedRuntimeLifecycle()
    {
        using var commands = new CommandDispatcher();
        var quickActions = new QuickActionRegistry();
        var activation = new FakeActivationGestureService();
        var nativeShell = new FakeNativeShell();
        var applicationShell = new FakeApplicationShell();
        var runtime = new LuvLetterRuntime(
            new FakeConfigurationStore(LuvLetterConfiguration.Default),
            commands,
            quickActions,
            [new SettingsModule(applicationShell)],
            activation,
            nativeShell,
            applicationShell);

        await runtime.StartAsync(CancellationToken.None);
        Assert.Equal(1, nativeShell.AppliedConfigurations);
        Assert.Equal(1, nativeShell.SynchronizedSnapshots.Count);
        Assert.Equal("settings.open", nativeShell.SynchronizedSnapshots[0].Single().Id);
        Assert.Equal(1, activation.AppliedOptions.Count);
        Assert.Equal(1, applicationShell.StartMinimizedCalls);

        activation.RaiseCommandInputRequested();
        activation.RaiseQuickActionsRequested();
        Assert.Equal(1, nativeShell.ToggleCommandInputCalls);
        Assert.Equal(1, nativeShell.ToggleQuickActionsCalls);

        nativeShell.RaiseQuickActionActivated("settings.open");
        await Task.Delay(25);
        Assert.Equal(1, applicationShell.ShowSettingsCalls);

        await runtime.StopAsync(CancellationToken.None);
        await runtime.StopAsync(CancellationToken.None);
        Assert.Equal(1, activation.StopCalls);
        Assert.Equal(1, nativeShell.HideCommandInputCalls);
        Assert.Equal(1, nativeShell.HideQuickActionsCalls);

        activation.RaiseQuickActionsRequested();
        Assert.Equal(
            1,
            nativeShell.ToggleQuickActionsCalls,
            "Runtime shutdown left an activation event subscribed.");
    }
}
