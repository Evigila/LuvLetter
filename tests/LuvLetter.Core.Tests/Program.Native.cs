using System.Reflection;
using System.Runtime.InteropServices;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Features;
using LuvLetter.Core.Native;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestManagedNativeAbiLayout()
    {
        var assembly = typeof(LuvLetterConfiguration).Assembly;
        AssertNativeLayout(
            assembly,
            "LuvLetter.Core.Native.NativeInputBoxConfig",
            104,
            [
                "StructSize", "AbiVersion", "Width", "Height", "CornerRadius",
                "BorderThickness", "FontSize", "HorizontalPadding", "VerticalPadding",
                "CaretWidth", "PositionMode", "OffsetX", "OffsetY", "BottomMargin",
                "CustomX", "CustomY", "BorderColor", "BackgroundColor", "TextColor",
                "CaretColor", "SubmitVirtualKey", "CancelVirtualKey", "BackspaceVirtualKey",
                "SubmitModifiers", "CancelModifiers", "BackspaceModifiers",
            ]);
        AssertNativeLayout(
            assembly,
            "LuvLetter.Core.Native.NativeFeatureWindowConfig",
            88,
            [
                "StructSize", "AbiVersion", "ItemsPerPage", "CellSize", "Gap",
                "CornerRadius", "BorderThickness", "FontSize", "BottomMargin", "OffsetX",
                "OffsetY", "BorderColor", "BackgroundColor", "TextColor", "AccentColor",
                "PreviousVirtualKey", "NextVirtualKey", "CancelVirtualKey",
                "FirstItemVirtualKey", "PreviousModifiers", "NextModifiers", "CancelModifiers",
            ]);
        AssertNativeLayout(
            assembly,
            "LuvLetter.Core.Native.NativeFeatureItem",
            16,
            ["Token", "Label"]);

        return Task.CompletedTask;
    }

    private static Task TestBoundedCallbackDispatcher()
    {
        using var consumerEntered = new ManualResetEventSlim();
        using var releaseConsumer = new ManualResetEventSlim();
        var received = new List<int>();
        using var dispatcher = new BoundedCallbackDispatcher<int>(
            capacity: 1,
            value =>
            {
                lock (received)
                {
                    received.Add(value);
                }

                if (value == 1)
                {
                    consumerEntered.Set();
                    releaseConsumer.Wait(TimeSpan.FromSeconds(2));
                }
            });

        Assert.True(dispatcher.TryEnqueue(1));
        Assert.True(
            consumerEntered.Wait(TimeSpan.FromSeconds(2)),
            "The callback dispatcher did not start its consumer.");
        Assert.True(dispatcher.TryEnqueue(2));
        Assert.False(
            dispatcher.TryEnqueue(3),
            "The callback dispatcher must reject work beyond its bounded capacity.");
        Assert.Equal(1L, dispatcher.DroppedCount);

        releaseConsumer.Set();
        Assert.True(
            SpinWait.SpinUntil(
                () =>
                {
                    lock (received)
                    {
                        return received.Count == 2;
                    }
                },
                TimeSpan.FromSeconds(2)),
            "The callback dispatcher did not drain accepted work.");

        lock (received)
        {
            Assert.SequenceEqual([1, 2], received);
        }

        return Task.CompletedTask;
    }

    private static async Task TestInputBoxServiceAdapter()
    {
        var nativeApi = new FakeNativeInputBoxApi();
        var service = new InputBoxService(nativeApi);
        try
        {
            Assert.Equal(1, nativeApi.CompatibilityChecks);
            Assert.NotNull(nativeApi.InputSubmittedCallback);
            Assert.NotNull(nativeApi.FeatureActivatedCallback);

            service.ApplyConfiguration(
                LuvLetterConfiguration.Default.InputBox,
                LuvLetterConfiguration.Default.FeatureWindow);
            Assert.Equal(nativeApi.AbiVersion, nativeApi.LastInputBoxConfig?.AbiVersion);
            Assert.Equal(nativeApi.AbiVersion, nativeApi.LastFeatureWindowConfig?.AbiVersion);
            Assert.Equal(560, nativeApi.LastInputBoxConfig?.Width);

            service.SynchronizeFeatures(
            [
                new FeatureItemSnapshot("alpha", "  Alpha\r\nfeature  "),
            ]);
            Assert.Equal(1, nativeApi.FeatureItems.Count);
            Assert.Equal("Alpha  feature", nativeApi.FeatureItems[0].Label);
            var alphaToken = nativeApi.FeatureItems[0].Token;

            var featureActivated = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.FeatureActivated += featureActivated.SetResult;
            nativeApi.RaiseFeatureActivated(alphaToken);
            Assert.Equal(
                "alpha",
                await featureActivated.Task.WaitAsync(TimeSpan.FromSeconds(2)));

            nativeApi.SetFeatureItemsResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(
                () => service.SynchronizeFeatures(
                [
                    new FeatureItemSnapshot("beta", "Beta"),
                ]));
            var failedBetaToken = nativeApi.FeatureItems[0].Token;

            var restoredFeatureActivated = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.FeatureActivated += restoredFeatureActivated.SetResult;
            nativeApi.RaiseFeatureActivated(alphaToken);
            Assert.Equal(
                "alpha",
                await restoredFeatureActivated.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            nativeApi.SetFeatureItemsResult = 0;
            service.SynchronizeFeatures(
            [
                new FeatureItemSnapshot("gamma", "Gamma"),
            ]);
            Assert.NotEqual(
                failedBetaToken,
                nativeApi.FeatureItems[0].Token,
                "A token observed by a failed Native synchronization must not be reused.");

            var inputSubmitted = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.InputSubmitted += inputSubmitted.SetResult;
            nativeApi.RaiseInputSubmitted("hello native");
            Assert.Equal(
                "hello native",
                await inputSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(2)));

            service.Show();
            service.Hide();
            service.ToggleFeatureWindow();
            Assert.Equal(1, nativeApi.ShowInputBoxCalls);
            Assert.Equal(1, nativeApi.HideInputBoxCalls);
            Assert.Equal(1, nativeApi.ToggleFeatureWindowCalls);

            nativeApi.ToggleInputBoxResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(service.Toggle);
        }
        finally
        {
            service.Dispose();
        }

        Assert.Equal(1, nativeApi.ShutdownCalls);
        Assert.True(nativeApi.InputSubmittedCallback is null);
        Assert.True(nativeApi.FeatureActivatedCallback is null);
    }

    private static void AssertNativeLayout(
        Assembly assembly,
        string typeName,
        int expectedSize,
        IReadOnlyList<string> expectedFields)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        Assert.Equal(expectedSize, Marshal.SizeOf(type), $"Unexpected size for {typeName}.");
        var actualFields = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(field => field.MetadataToken)
            .Select(field => field.Name);
        Assert.SequenceEqual(expectedFields, actualFields, $"Unexpected field order for {typeName}.");
    }
}
