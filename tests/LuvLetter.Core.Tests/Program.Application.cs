using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.Settings;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestConfigurationApplicationTransaction()
    {
        var previous = LuvLetterConfigurationStore.Normalize(LuvLetterConfiguration.Default);
        var requested = previous with
        {
            InputBox = previous.InputBox with
            {
                Size = previous.InputBox.Size with { Width = 777 },
            },
        };

        var successfulStore = new FakeConfigurationStore(previous);
        var successfulGestures = new FakeActivationGestureService();
        var successfulNative = new FakeInputBoxConfigurationSink();
        var successfulService = new SettingsService(
            successfulStore,
            successfulGestures,
            successfulNative);

        var success = successfulService.Apply(requested);
        Assert.True(success.Succeeded);
        Assert.Equal(ConfigurationApplicationStatus.Applied, success.Status);
        Assert.False(success.RequiresRestart);
        Assert.Equal(777, successfulStore.Current.InputBox.Size.Width);
        Assert.Equal(1, successfulNative.AppliedConfigurations.Count);
        Assert.Equal(1, successfulGestures.AppliedOptions.Count);

        var failingStore = new FakeConfigurationStore(previous)
        {
            FailUpdates = true,
        };
        var rollbackGestures = new FakeActivationGestureService();
        var rollbackNative = new FakeInputBoxConfigurationSink();
        var rollbackService = new SettingsService(
            failingStore,
            rollbackGestures,
            rollbackNative);

        var failure = rollbackService.Apply(requested);
        Assert.False(failure.Succeeded);
        Assert.Equal(ConfigurationApplicationStatus.FailedAndRestored, failure.Status);
        Assert.False(failure.RequiresRestart);
        Assert.True(
            ReferenceEquals(previous, failingStore.Current),
            "A persistence failure must not publish the requested configuration.");
        Assert.Equal(2, rollbackNative.AppliedConfigurations.Count);
        Assert.Equal(777, rollbackNative.AppliedConfigurations[0].InputBox.Size.Width);
        Assert.Equal(560, rollbackNative.AppliedConfigurations[1].InputBox.Size.Width);
        Assert.Equal(2, rollbackGestures.AppliedOptions.Count);
        Assert.True(
            ReferenceEquals(previous, failure.DisplayConfiguration),
            "The settings UI must be restored to the previous configuration.");

        var inconsistentStore = new FakeConfigurationStore(previous)
        {
            FailUpdates = true,
        };
        var inconsistentNative = new FakeInputBoxConfigurationSink
        {
            FailOnApplyCall = 2,
        };
        var inconsistentService = new SettingsService(
            inconsistentStore,
            new FakeActivationGestureService(),
            inconsistentNative);

        var inconsistent = inconsistentService.Apply(requested);
        Assert.False(inconsistent.Succeeded);
        Assert.True(inconsistent.RequiresRestart);
        Assert.Equal(
            ConfigurationApplicationStatus.FailedRuntimeInconsistent,
            inconsistent.Status);
        Assert.True(
            inconsistent.Message.Contains("restart LuvLetter", StringComparison.Ordinal),
            "An incomplete rollback must explicitly require a restart.");

        return Task.CompletedTask;
    }
}
