namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("Default activation gestures", TestDefaultActivationGestures),
        ("Ctrl gesture default double tap", TestCtrlGestureDefaultDoubleTap),
        ("Legacy Ctrl hold is inactive", TestCtrlGestureHoldIsInactive),
        ("Ctrl gesture cancellation boundaries", TestCtrlGestureCancellationBoundaries),
        ("Ctrl gesture side and auto-repeat handling", TestCtrlGestureSidesAndAutoRepeat),
        ("Global Alt+F1, Alt+Backspace, and Escape shortcuts", TestGlobalShortcuts),
        ("Configuration normalization", TestConfigurationNormalization),
        ("Quick-action registry", TestQuickActionRegistry),
        ("Asynchronous command dispatch", TestCommandDispatcher),
        ("Managed native ABI layout", TestManagedNativeAbiLayout),
        ("Bounded native callback dispatcher", TestBoundedCallbackDispatcher),
        ("Native shell service adapter", TestNativeShellServiceAdapter),
        ("Configuration application transaction", TestConfigurationApplicationTransaction),
        ("Settings editor mapping", TestSettingsEditorMapping),
        ("Application coordinator lifecycle", TestApplicationCoordinatorLifecycle),
        ("Input mode routing", TestInputModeRouting),
        ("Input candidate routing and activation", TestInputCandidates),
        ("Plugin loading and registration isolation", TestPluginLoader),
        ("Configuration store round-trip and migration", TestConfigurationStore),
        ("Index ignore default validation", TestIndexIgnoreDefaults),
        ("Legacy index ignore default migration", TestLegacyIndexIgnoreUpgrade),
        ("Customized index ignore preservation", TestCustomIndexIgnoreConfiguration),
        ("Application name matching", TestApplicationNameMatching),
        ("Application partition cache fallback", TestApplicationPartitionCacheFallback),
        ("Application and file candidate ranking", TestApplicationCandidateRanking),
        ("Application candidate merge and refresh", TestApplicationCandidateMerge),
        ("Application candidate source isolation and modes", TestApplicationCandidateIsolation),
        ("Application candidate priority override", TestApplicationCandidatePriorityOverride),
        ("Application activation revision and cancellation", TestApplicationActivationRevision),
        ("Standalone executable candidate priority", TestStandaloneExecutablePriority),
        ("Index partition metadata", TestIndexPartitionMetadata),
        ("Index partition ownership", TestIndexPartitionOwnership),
        ("Index partition scheduling", TestIndexPartitionScheduling),
        ("Index partition read views", TestIndexPartitionReadViews),
        ("File index partition configuration", TestFileIndexPartitionConfiguration),
        ("File index progress protocol", TestFileIndexProgressProtocol),
    ];

    public static async Task<int> Main()
    {
        var passed = 0;
        foreach (var test in Tests)
        {
            try
            {
                await test.Run().ConfigureAwait(false);
                passed++;
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL  {test.Name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{passed}/{Tests.Length} smoke tests passed.");
        return passed == Tests.Length ? 0 : 1;
    }
}
