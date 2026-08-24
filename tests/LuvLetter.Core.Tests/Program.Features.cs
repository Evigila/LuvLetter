using LuvLetter.Core.Features;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static async Task TestFeatureRegistry()
    {
        var registry = new FeatureRegistry();
        var changedCount = 0;
        var activations = new List<int>();

        // A failing plug-in listener must not prevent later listeners from observing changes.
        registry.Changed += static (_, _) => throw new InvalidOperationException("listener failure");
        registry.Changed += (_, _) => changedCount++;

        var initialFeatures = Enumerable.Range(1, 9)
            .Select(number =>
                new FeatureDefinition(
                    $"test-{number}",
                    $"Test feature {number}",
                    () => activations.Add(number)))
            .ToArray();
        Assert.True(registry.RegisterRange(initialFeatures));

        Assert.Equal(1, changedCount, "A registration batch must publish one change.");
        Assert.SequenceEqual(
            Enumerable.Range(1, 9).Select(number => $"test-{number}"),
            registry.Snapshot().Select(feature => feature.Id),
            "Registration order changed.");

        Assert.False(
            registry.Register(
                new FeatureDefinition("test-4", "Rejected duplicate", static () => { })));
        Assert.Equal(1, changedCount, "A rejected duplicate must not publish Changed.");

        Assert.False(
            registry.RegisterRange(
            [
                new FeatureDefinition("test-11", "First duplicate", static () => { }),
                new FeatureDefinition("test-11", "Second duplicate", static () => { }),
            ]));
        Assert.False(
            registry.Snapshot().Any(feature => feature.Id == "test-11"),
            "A rejected batch partially changed the registry.");

        Assert.True(
            registry.Register(
                new FeatureDefinition(
                    "test-4",
                    "Replacement feature 4",
                    () => activations.Add(40)),
                FeatureRegistrationMode.ReplaceExisting));
        Assert.Equal(2, changedCount);
        Assert.Equal("test-4", registry.Snapshot()[3].Id);
        Assert.Equal("Replacement feature 4", registry.Snapshot()[3].DisplayName);

        Assert.Equal(
            FeatureActivationStatus.Succeeded,
            (await registry.ActivateAsync("test-4")).Status);
        Assert.Equal(
            FeatureActivationStatus.Succeeded,
            (await registry.ActivateAsync("test-1")).Status);
        Assert.SequenceEqual([40, 1], activations);
        Assert.Equal(
            FeatureActivationStatus.NotFound,
            (await registry.ActivateAsync("missing")).Status);

        Assert.True(
            registry.Register(
                new FeatureDefinition(
                    "test-5",
                    "Throwing feature 5",
                    static () => throw new InvalidOperationException("feature failure")),
                FeatureRegistrationMode.ReplaceExisting));
        var failedActivation = await registry.ActivateAsync("test-5");
        Assert.Equal(FeatureActivationStatus.Failed, failedActivation.Status);
        Assert.True(
            failedActivation.Exception is InvalidOperationException,
            "Feature exceptions must be retained for diagnostics.");
        Assert.Equal(
            FeatureActivationStatus.Succeeded,
            (await registry.ActivateAsync("test-6")).Status,
            "A failed feature must not poison the registry.");
        Assert.SequenceEqual([40, 1, 6], activations);

        Assert.True(registry.Unregister("test-2"));
        Assert.True(
            registry.Register(
                new FeatureDefinition("test-10", "Dynamic feature 10", static () => { })));
        Assert.Equal(5, changedCount, "Dynamic replacement/removal/addition must publish Changed.");
        Assert.Equal(9, registry.Snapshot().Count);
        Assert.Equal("test-10", registry.Snapshot()[^1].Id);

        var beforeThrowingBatch = registry.Snapshot().Select(feature => feature.Id).ToArray();
        Assert.Throws<InvalidOperationException>(
            () => registry.RegisterRange(CreateThrowingFeatureBatch()));
        Assert.SequenceEqual(
            beforeThrowingBatch,
            registry.Snapshot().Select(feature => feature.Id),
            "An enumeration failure partially registered a batch.");

        Assert.True(
            registry.Register(
                new FeatureDefinition(
                    "test-cancel",
                    "Cancelable feature",
                    async cancellationToken =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    })));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(
            FeatureActivationStatus.Canceled,
            (await registry.ActivateAsync("test-cancel", cancellation.Token)).Status);

        static IEnumerable<FeatureDefinition> CreateThrowingFeatureBatch()
        {
            yield return new FeatureDefinition("never-committed", "Never committed", static () => { });
            throw new InvalidOperationException("batch enumeration failure");
        }
    }
}
