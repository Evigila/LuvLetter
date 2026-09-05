using LuvLetter.Core.Commands;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.Plugins;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestPluginLoader()
    {
        TestDiscoveryWarningsAndCleanup();
        TestInvalidAssemblyIsolation();
        TestCollectionAndValidationIsolation();
        TestUnexpectedCommitFailureCleanup();
        return Task.CompletedTask;
    }

    private static void TestDiscoveryWarningsAndCleanup()
    {
        var absentDirectory = CreateAbsentPluginsDirectory();
        var accepted = new FakePlugin("alpha");
        var duplicate = new FakePlugin("alpha");
        var unnamed = new FakePlugin(" ");
        using var commandDispatcher = new CommandDispatcher();
        var quickActionRegistry = new QuickActionRegistry();

        using (var session = PluginLoader.Load(
        [
            accepted,
            duplicate,
            unnamed,
        ],
            commandDispatcher,
            quickActionRegistry,
            absentDirectory))
        {
            Assert.Equal(1, session.RegisteredPluginIds.Count);
            Assert.Equal("alpha", session.RegisteredPluginIds[0]);
            Assert.Equal(2, session.Warnings.Count);
            Assert.False(accepted.IsDisposed);
            Assert.True(duplicate.IsDisposed);
            Assert.True(unnamed.IsDisposed);
        }

        Assert.True(accepted.IsDisposed);

        var enumeratedBeforeFailure = new FakePlugin("enumerated-before-failure");
        Assert.Throws<InvalidOperationException>(
            () => PluginLoader.Load(
                CreateThrowingPluginSequence(enumeratedBeforeFailure),
                commandDispatcher,
                quickActionRegistry,
                absentDirectory));
        Assert.True(enumeratedBeforeFailure.IsDisposed);
    }

    private static void TestInvalidAssemblyIsolation()
    {
        var invalidPluginDirectory = Path.Combine(
            Path.GetTempPath(),
            $"LuvLetter-invalid-plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidPluginDirectory);
        try
        {
            File.WriteAllText(Path.Combine(invalidPluginDirectory, "invalid.dll"), "not an assembly");
            using var commandDispatcher = new CommandDispatcher();
            var quickActionRegistry = new QuickActionRegistry();
            using var session = PluginLoader.Load(
                [new FakePlugin("builtin")],
                commandDispatcher,
                quickActionRegistry,
                invalidPluginDirectory);

            Assert.Equal(1, session.RegisteredPluginIds.Count);
            Assert.True(
                session.Warnings.Any(
                    warning => warning.Contains("invalid.dll", StringComparison.Ordinal)),
                "An invalid optional plugin must be isolated as a discovery warning.");
        }
        finally
        {
            Directory.Delete(invalidPluginDirectory, recursive: true);
        }
    }

    private static void TestCollectionAndValidationIsolation()
    {
        using var commandDispatcher = new CommandDispatcher();
        var quickActionRegistry = new QuickActionRegistry();
        var failingPlugin = new FakePlugin(
            "failing",
            context =>
            {
                context.RegisterCommand("test", "partial", _ => { });
                context.RegisterQuickAction("partial.action", "Partial", () => { });
                throw new InvalidOperationException("planned registration failure");
            });
        var healthyPlugin = new FakePlugin(
            "healthy",
            context =>
            {
                context.RegisterCommand("test", "healthy run", _ => { });
                context.RegisterCommandAlias(
                    "test", "well", "test", "healthy run");
                context.RegisterCommandLink(
                    "test", "go", "test", "healthy");
                context.RegisterQuickAction("healthy.action", "Healthy", () => { });
            });
        var conflictingPlugin = new FakePlugin(
            "conflicting",
            context => context.RegisterCommand("test", "healthy run", _ => { }));

        using (var session = PluginLoader.Load(
        [
            failingPlugin,
            healthyPlugin,
            conflictingPlugin,
        ],
            commandDispatcher,
            quickActionRegistry,
            CreateAbsentPluginsDirectory()))
        {
            Assert.SequenceEqual(["healthy"], session.RegisteredPluginIds);
            Assert.Equal(2, session.Warnings.Count);
            Assert.True(failingPlugin.IsDisposed);
            Assert.False(healthyPlugin.IsDisposed);
            Assert.True(conflictingPlugin.IsDisposed);
            Assert.False(commandDispatcher.IsRegistered("test", "partial"));
            Assert.False(quickActionRegistry.IsRegistered("partial.action"));
            Assert.True(commandDispatcher.IsRegistered("test", "healthy run"));
            Assert.True(commandDispatcher.IsExecutable("test", "well"));
            Assert.True(commandDispatcher.IsExecutable("test", "go run"));
            Assert.True(quickActionRegistry.IsRegistered("healthy.action"));
        }

        Assert.True(healthyPlugin.IsDisposed);
    }

    private static void TestUnexpectedCommitFailureCleanup()
    {
        var plugin = new FakePlugin(
            "fatal",
            context => context.RegisterCommand("test", "fatal", _ => { }));
        var commandRegistrar = new RejectingCommandRegistrar();
        var quickActionRegistry = new QuickActionRegistry();

        Assert.Throws<InvalidOperationException>(
            () => PluginLoader.Load(
                [plugin],
                commandRegistrar,
                quickActionRegistry,
                CreateAbsentPluginsDirectory()));
        Assert.True(plugin.IsDisposed);
        Assert.Equal(1, commandRegistrar.RegisterCalls);
    }

    private static string CreateAbsentPluginsDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            $"LuvLetter-missing-plugins-{Guid.NewGuid():N}");

    private static IEnumerable<ILuvLetterPlugin> CreateThrowingPluginSequence(
        ILuvLetterPlugin first)
    {
        yield return first;
        throw new IOException("planned built-in enumeration failure");
    }

    private sealed class RejectingCommandRegistrar : ICommandRegistrar
    {
        public int RegisterCalls { get; private set; }

        public bool Register(
            string commandDomain,
            string commandName,
            Action<CommandInvocation> handler,
            CommandRegistrationMode mode = CommandRegistrationMode.RejectDuplicate)
        {
            _ = commandDomain;
            _ = commandName;
            _ = handler;
            _ = mode;
            RegisterCalls++;
            return false;
        }

        public bool IsRegistered(string commandDomain, string commandName)
        {
            _ = commandDomain;
            _ = commandName;
            return false;
        }

        public bool RegisterAlias(
            string aliasDomain,
            string aliasPath,
            string targetDomain,
            string targetPath) => false;

        public bool RegisterLink(
            string sourceDomain,
            string sourcePath,
            string targetDomain,
            string targetPath) => false;

        public bool IsExecutable(string commandDomain, string commandPath) => false;

        public bool HasPath(string commandDomain, string commandPath) => false;
    }
}
