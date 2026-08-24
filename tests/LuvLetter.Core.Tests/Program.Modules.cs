using LuvLetter.Core.Commands;
using LuvLetter.Core.Features;
using LuvLetter.Core.Modules;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestModuleCatalog()
    {
        var absentDirectory = Path.Combine(
            Path.GetTempPath(),
            $"LuvLetter-missing-modules-{Guid.NewGuid():N}");
        var builtIns = ModuleCatalog.Discover(
        [
            new FakeModule("alpha"),
            new FakeModule("alpha"),
            new FakeModule(" "),
        ], absentDirectory);
        Assert.Equal(1, builtIns.Modules.Count);
        Assert.Equal("alpha", builtIns.Modules[0].Id);
        Assert.Equal(2, builtIns.Warnings.Count);

        var invalidModuleDirectory = Path.Combine(
            Path.GetTempPath(),
            $"LuvLetter-invalid-modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidModuleDirectory);
        try
        {
            File.WriteAllText(Path.Combine(invalidModuleDirectory, "invalid.dll"), "not an assembly");
            var invalidAssembly = ModuleCatalog.Discover(
                [new FakeModule("builtin")],
                invalidModuleDirectory);
            Assert.Equal(1, invalidAssembly.Modules.Count);
            Assert.True(
                invalidAssembly.Warnings.Any(
                    warning => warning.Contains("invalid.dll", StringComparison.Ordinal)),
                "An invalid optional module must be isolated as a discovery warning.");
        }
        finally
        {
            Directory.Delete(invalidModuleDirectory, recursive: true);
        }

        using var commandDispatcher = new CommandDispatcher();
        var featureRegistry = new FeatureRegistry();
        var failingModule = new FakeModule(
            "failing",
            context =>
            {
                context.RegisterCommand("partial", _ => { });
                context.RegisterFeature("partial.feature", "Partial", () => { });
                throw new InvalidOperationException("planned registration failure");
            });
        var healthyModule = new FakeModule(
            "healthy",
            context =>
            {
                context.RegisterCommand("healthy", _ => { });
                context.RegisterFeature("healthy.feature", "Healthy", () => { });
            });

        using (var registration = ModuleRegistrar.Register(
        [
            failingModule,
            healthyModule,
        ],
            commandDispatcher,
            featureRegistry,
            () => { }))
        {
            Assert.Equal(1, registration.RegisteredModuleIds.Count);
            Assert.Equal("healthy", registration.RegisteredModuleIds[0]);
            Assert.Equal(1, registration.Warnings.Count);
            Assert.True(failingModule.IsDisposed);
            Assert.False(healthyModule.IsDisposed);
            Assert.False(commandDispatcher.IsRegistered("partial"));
            Assert.False(featureRegistry.IsRegistered("partial.feature"));
            Assert.True(commandDispatcher.IsRegistered("healthy"));
            Assert.True(featureRegistry.IsRegistered("healthy.feature"));
        }

        Assert.True(healthyModule.IsDisposed);

        return Task.CompletedTask;
    }
}
