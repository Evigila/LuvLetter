using LuvLetter.Core.Modules.QuickActions;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static async Task TestQuickActionRegistry()
    {
        var registry = new QuickActionRegistry();
        var changedCount = 0;
        var activations = new List<int>();

        // A failing plug-in listener must not prevent later listeners from observing changes.
        registry.Changed += static (_, _) => throw new InvalidOperationException("listener failure");
        registry.Changed += (_, _) => changedCount++;

        var initialQuickActions = Enumerable.Range(1, 9)
            .Select(number =>
                new QuickActionDefinition(
                    $"test-{number}",
                    $"Test quick action {number}",
                    () => activations.Add(number)))
            .ToArray();
        Assert.True(registry.RegisterRange(initialQuickActions));

        Assert.Equal(1, changedCount, "A registration batch must publish one change.");
        Assert.SequenceEqual(
            Enumerable.Range(1, 9).Select(number => $"test-{number}"),
            registry.Snapshot().Select(quickAction => quickAction.Id),
            "Registration order changed.");

        Assert.False(
            registry.Register(
                new QuickActionDefinition("test-4", "Rejected duplicate", static () => { })));
        Assert.Equal(1, changedCount, "A rejected duplicate must not publish Changed.");

        Assert.False(
            registry.RegisterRange(
            [
                new QuickActionDefinition("test-11", "First duplicate", static () => { }),
                new QuickActionDefinition("test-11", "Second duplicate", static () => { }),
            ]));
        Assert.False(
            registry.Snapshot().Any(quickAction => quickAction.Id == "test-11"),
            "A rejected batch partially changed the registry.");

        Assert.True(
            registry.Register(
                new QuickActionDefinition(
                    "test-4",
                    "Replacement quick action 4",
                    () => activations.Add(40)),
                QuickActionRegistrationMode.ReplaceExisting));
        Assert.Equal(2, changedCount);
        Assert.Equal("test-4", registry.Snapshot()[3].Id);
        Assert.Equal("Replacement quick action 4", registry.Snapshot()[3].DisplayName);

        Assert.Equal(
            QuickActionActivationStatus.Succeeded,
            (await registry.ActivateAsync("test-4")).Status);
        Assert.Equal(
            QuickActionActivationStatus.Succeeded,
            (await registry.ActivateAsync("test-1")).Status);
        Assert.SequenceEqual([40, 1], activations);
        Assert.Equal(
            QuickActionActivationStatus.NotFound,
            (await registry.ActivateAsync("missing")).Status);

        Assert.True(
            registry.Register(
                new QuickActionDefinition(
                    "test-5",
                    "Throwing quick action 5",
                    static () => throw new InvalidOperationException("quick-action failure")),
                QuickActionRegistrationMode.ReplaceExisting));
        var failedActivation = await registry.ActivateAsync("test-5");
        Assert.Equal(QuickActionActivationStatus.Failed, failedActivation.Status);
        Assert.True(
            failedActivation.Exception is InvalidOperationException,
            "Quick-action exceptions must be retained for diagnostics.");
        Assert.Equal(
            QuickActionActivationStatus.Succeeded,
            (await registry.ActivateAsync("test-6")).Status,
            "A failed quick action must not poison the registry.");
        Assert.SequenceEqual([40, 1, 6], activations);

        Assert.True(registry.Unregister("test-2"));
        Assert.True(
            registry.Register(
                new QuickActionDefinition("test-10", "Dynamic quick action 10", static () => { })));
        Assert.Equal(5, changedCount, "Dynamic replacement/removal/addition must publish Changed.");
        Assert.Equal(9, registry.Snapshot().Count);
        Assert.Equal("test-10", registry.Snapshot()[^1].Id);

        var beforeThrowingBatch = registry.Snapshot()
            .Select(quickAction => quickAction.Id)
            .ToArray();
        Assert.Throws<InvalidOperationException>(
            () => registry.RegisterRange(CreateThrowingQuickActionBatch()));
        Assert.SequenceEqual(
            beforeThrowingBatch,
            registry.Snapshot().Select(quickAction => quickAction.Id),
            "An enumeration failure partially registered a batch.");

        Assert.True(
            registry.Register(
                new QuickActionDefinition(
                    "test-cancel",
                    "Cancelable quick action",
                    async cancellationToken =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    })));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(
            QuickActionActivationStatus.Canceled,
            (await registry.ActivateAsync("test-cancel", cancellation.Token)).Status);

        static IEnumerable<QuickActionDefinition> CreateThrowingQuickActionBatch()
        {
            yield return new QuickActionDefinition(
                "never-committed",
                "Never committed",
                static () => { });
            throw new InvalidOperationException("batch enumeration failure");
        }
    }
}
