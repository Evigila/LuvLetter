using LuvLetter.Core.Application;
using LuvLetter.Core.Commands;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.Indexing;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.Modules.Settings;
using LuvLetter.Core.NativeShell;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static async Task TestInputModeRouting()
    {
        using var commands = new CommandDispatcher();
        var systemCommands = new FakeSystemCommandRunner();
        var quickActions = new QuickActionRegistry();
        var activation = new FakeActivationGestureService();
        var nativeShell = new FakeNativeShell();
        var applicationShell = new FakeApplicationShell();
        var indexRefreshRequester = new CountingIndexRefreshRequester();
        var matcher = new FakeGeneralInputMatcher(
            input => string.Equals(input, "file.txt", StringComparison.Ordinal));
        var coordinator = new ApplicationCoordinator(
            new FakeConfigurationStore(LuvLetterConfiguration.Default),
            commands,
            systemCommands,
            quickActions,
            [new SettingsPlugin(applicationShell), new IndexingPlugin(indexRefreshRequester)],
            [matcher],
            activation,
            nativeShell,
            applicationShell);

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            nativeShell.RaiseInputSubmitted("/luv settings", InputMode.Ask);
            Assert.SequenceEqual(["Echo: /luv settings"], nativeShell.EnqueuedMessages);
            Assert.Equal(0, applicationShell.ShowSettingsCalls);

            nativeShell.RaiseInputSubmitted("file.txt", InputMode.General);
            Assert.SequenceEqual(["file.txt"], matcher.Inputs);
            Assert.Equal(
                1,
                nativeShell.EnqueuedMessages.Count,
                "A handled General input must not fall through to Echo.");

            nativeShell.RaiseInputSubmitted("luv settings", InputMode.General);
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

            nativeShell.RaiseInputSubmitted("/luv missing argument", InputMode.Command);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.EnqueuedMessages.Contains("Unknown command: luv missing argument"),
                    TimeSpan.FromSeconds(2)),
                "Command mode did not preserve strict unknown-command reporting.");

            nativeShell.RaiseInputSubmitted("/luv settings", InputMode.Command);
            Assert.True(
                SpinWait.SpinUntil(
                    () => applicationShell.ShowSettingsCalls == 2,
                    TimeSpan.FromSeconds(2)),
                "Command mode did not execute a registered command.");
            Assert.SequenceEqual(
                ["file.txt", "plain question"],
                matcher.Inputs,
                "Ask and Command modes must bypass General matchers.");

            nativeShell.RaiseInputSubmitted("/luv index.refresh", InputMode.Command);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.EnqueuedMessages.Contains("Unknown command: luv index.refresh"),
                    TimeSpan.FromSeconds(2)),
                "The retired dotted command syntax was still accepted.");
            Assert.Equal(0, indexRefreshRequester.Requests);

            nativeShell.RaiseInputSubmitted("/luv index refresh", InputMode.Command);
            Assert.True(
                SpinWait.SpinUntil(
                    () => indexRefreshRequester.Requests == 1,
                    TimeSpan.FromSeconds(2)),
                "The slash shortcut did not execute the built-in index refresh command.");
            Assert.SequenceEqual(
                [IndexRefreshMode.Normal],
                indexRefreshRequester.Modes);

            nativeShell.RaiseInputSubmitted("/luv index refresh -f", InputMode.Command);
            nativeShell.RaiseInputSubmitted("/luv refreshindex --force", InputMode.Command);
            Assert.True(
                SpinWait.SpinUntil(
                    () => indexRefreshRequester.Requests == 3,
                    TimeSpan.FromSeconds(2)),
                "Force refresh or its command link did not execute.");
            Assert.SequenceEqual(
                [IndexRefreshMode.Normal, IndexRefreshMode.Force, IndexRefreshMode.Force],
                indexRefreshRequester.Modes);

            nativeShell.RaiseInputSubmitted("/luv index refresh --unknown", InputMode.Command);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.EnqueuedMessages.Any(message => message.Contains(
                        "Usage: /luv index refresh [-f|--force]",
                        StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(2)),
                "An invalid refresh flag did not report command usage.");
            Assert.Equal(3, indexRefreshRequester.Requests);

            nativeShell.RaiseInputSubmitted("/", InputMode.Command);
            Assert.Equal(
                "Command was not accepted: RejectedEmpty",
                nativeShell.EnqueuedMessages[^1]);

            nativeShell.RaiseInputSubmitted("/echo hello", InputMode.Command);
            Assert.SequenceEqual(
                ["echo hello"],
                systemCommands.Requests.Select(static request => request.CommandText));
            Assert.Equal(0, nativeShell.HideCommandInputCalls);

            var request = systemCommands.Requests.Single();
            systemCommands.RaiseStarted(request.RequestId, request.CommandText);
            Assert.Equal("正在运行：echo hello", nativeShell.BegunMessageActivities[^1]);
            systemCommands.RaiseCompleted(new(
                request.RequestId,
                request.CommandText,
                0,
                "hello",
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(20)));
            Assert.Equal("hello", nativeShell.CompletedMessageActivities[^1]);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CountingIndexRefreshRequester : IIndexRefreshRequester
    {
        private readonly List<IndexRefreshMode> requests = [];

        public int Requests
        {
            get
            {
                lock (requests) return requests.Count;
            }
        }

        public IReadOnlyList<IndexRefreshMode> Modes
        {
            get
            {
                lock (requests) return requests.ToArray();
            }
        }

        public void RequestRefresh(IndexRefreshMode mode)
        {
            lock (requests) requests.Add(mode);
        }
    }
}
