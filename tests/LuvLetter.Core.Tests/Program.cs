namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("Default activation gestures", TestDefaultActivationGestures),
        ("Ctrl gesture default double tap", TestCtrlGestureDefaultDoubleTap),
        ("Ctrl gesture default tap then hold", TestCtrlGestureDefaultTapThenHold),
        ("Ctrl gesture cancellation boundaries", TestCtrlGestureCancellationBoundaries),
        ("Ctrl gesture side and auto-repeat handling", TestCtrlGestureSidesAndAutoRepeat),
        ("Ctrl gesture configurable mapping", TestCtrlGestureConfigurableMapping),
        ("Configuration normalization", TestConfigurationNormalization),
        ("Quick-action registry", TestQuickActionRegistry),
        ("Asynchronous command dispatch", TestCommandDispatcher),
        ("Managed native ABI layout", TestManagedNativeAbiLayout),
        ("Bounded native callback dispatcher", TestBoundedCallbackDispatcher),
        ("Native shell service adapter", TestNativeShellServiceAdapter),
        ("Configuration application transaction", TestConfigurationApplicationTransaction),
        ("Settings editor mapping", TestSettingsEditorMapping),
        ("Hosted runtime lifecycle", TestHostedRuntimeLifecycle),
        ("Plugin loading and registration isolation", TestPluginLoader),
        ("Configuration store round-trip and migration", TestConfigurationStore),
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
