using LuvLetter.Core.Application;
using LuvLetter.Core.Commands;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.Modules.Settings;
using LuvLetter.Core.NativeShell;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static async Task TestInputModeRouting()
    {
        using var commands = new CommandDispatcher();
        var quickActions = new QuickActionRegistry();
        var activation = new FakeActivationGestureService();
        var nativeShell = new FakeNativeShell();
        var applicationShell = new FakeApplicationShell();
        var matcher = new FakeGeneralInputMatcher(
            input => string.Equals(input, "file.txt", StringComparison.Ordinal));
        var coordinator = new ApplicationCoordinator(
            new FakeConfigurationStore(LuvLetterConfiguration.Default),
            commands,
            quickActions,
            [new SettingsPlugin(applicationShell)],
            [matcher],
            activation,
            nativeShell,
            applicationShell);

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            nativeShell.RaiseInputSubmitted("settings", InputMode.Ask);
            Assert.SequenceEqual(["Echo: settings"], nativeShell.EnqueuedMessages);
            Assert.Equal(0, applicationShell.ShowSettingsCalls);

            nativeShell.RaiseInputSubmitted("file.txt", InputMode.General);
            Assert.SequenceEqual(["file.txt"], matcher.Inputs);
            Assert.Equal(
                1,
                nativeShell.EnqueuedMessages.Count,
                "A handled General input must not fall through to Echo.");

            nativeShell.RaiseInputSubmitted("settings", InputMode.General);
            Assert.True(
                SpinWait.SpinUntil(
                    () => applicationShell.ShowSettingsCalls == 1,
                    TimeSpan.FromSeconds(2)),
                "General mode did not execute a registered command.");
            Assert.SequenceEqual(
                ["file.txt"],
                matcher.Inputs,
                "Registered commands must be routed before General matchers.");

            nativeShell.RaiseInputSubmitted("plain question", InputMode.General);
            Assert.SequenceEqual(["file.txt", "plain question"], matcher.Inputs);
            Assert.Equal("Echo: plain question", nativeShell.EnqueuedMessages[^1]);

            nativeShell.RaiseInputSubmitted("missing argument", InputMode.Command);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.EnqueuedMessages.Contains("Unknown command: missing"),
                    TimeSpan.FromSeconds(2)),
                "Command mode did not preserve strict unknown-command reporting.");

            nativeShell.RaiseInputSubmitted("settings", InputMode.Command);
            Assert.True(
                SpinWait.SpinUntil(
                    () => applicationShell.ShowSettingsCalls == 2,
                    TimeSpan.FromSeconds(2)),
                "Command mode did not execute a registered command.");
            Assert.SequenceEqual(
                ["file.txt", "plain question"],
                matcher.Inputs,
                "Ask and Command modes must bypass General matchers.");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }
}
