using System.Reflection;
using System.Runtime.InteropServices;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.NativeShell;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestManagedNativeAbiLayout()
    {
        var assembly = typeof(LuvLetterConfiguration).Assembly;
        AssertNativeLayout(
            assembly,
            "LuvLetter.Core.NativeShell.NativeInputBoxConfig",
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
            "LuvLetter.Core.NativeShell.NativeFeatureWindowConfig",
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
            "LuvLetter.Core.NativeShell.NativeFeatureItem",
            16,
            ["Token", "Label"]);
        AssertNativeLayout(
            assembly,
            "LuvLetter.Core.NativeShell.NativeInputCandidate",
            48,
            ["Token", "Kind", "IconKind", "Actions", "PrimaryText", "SecondaryText", "IconSource"]);

        return Task.CompletedTask;
    }

    private static Task TestBoundedCallbackDispatcher()
    {
        using var consumerEntered = new ManualResetEventSlim();
        using var releaseConsumer = new ManualResetEventSlim();
        using var failingConsumerEntered = new ManualResetEventSlim();
        using var releaseFailingConsumer = new ManualResetEventSlim();
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

                if (value == 4)
                {
                    failingConsumerEntered.Set();
                    releaseFailingConsumer.Wait(TimeSpan.FromSeconds(2));
                    throw new InvalidOperationException("simulated callback consumer failure");
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

        Assert.True(dispatcher.TryEnqueue(4));
        Assert.True(
            failingConsumerEntered.Wait(TimeSpan.FromSeconds(2)),
            "The callback dispatcher did not start the failing consumer.");
        Assert.True(dispatcher.TryEnqueue(5));
        releaseFailingConsumer.Set();
        Assert.True(
            SpinWait.SpinUntil(
                () =>
                {
                    lock (received)
                    {
                        return received.Count == 4;
                    }
                },
                TimeSpan.FromSeconds(2)),
            "A consumer exception terminated the callback queue drain.");

        lock (received)
        {
            Assert.SequenceEqual([1, 2, 4, 5], received);
        }

        dispatcher.Dispose();
        Assert.False(dispatcher.TryEnqueue(6));
        Assert.Equal(
            1L,
            dispatcher.DroppedCount,
            "Disposal rejections must not be counted as capacity drops.");

        return Task.CompletedTask;
    }

    private static async Task TestNativeShellServiceAdapter()
    {
        var nativeApi = new FakeNativeShellApi();
        var service = new NativeShellService(nativeApi);
        try
        {
            Assert.Equal(12U, nativeApi.AbiVersion);
            Assert.Equal(1, nativeApi.CompatibilityChecks);
            Assert.NotNull(nativeApi.InputSubmittedCallback);
            Assert.NotNull(nativeApi.InputChangedCallback);
            Assert.NotNull(nativeApi.CandidateActivatedCallback);
            Assert.NotNull(nativeApi.QuickActionActivatedCallback);

            service.ApplyConfiguration(
                LuvLetterConfiguration.Default.InputBox with
                {
                    Size = LuvLetterConfiguration.Default.InputBox.Size with { FontSize = 20.0f },
                },
                LuvLetterConfiguration.Default.QuickActions with
                {
                    Layout = LuvLetterConfiguration.Default.QuickActions.Layout with
                    {
                        FontSize = 22.0f,
                    },
                });
            Assert.Equal(nativeApi.AbiVersion, nativeApi.LastInputBoxConfig?.AbiVersion);
            Assert.Equal(nativeApi.AbiVersion, nativeApi.LastQuickActionsConfig?.AbiVersion);
            Assert.Equal(560, nativeApi.LastInputBoxConfig?.Width);
            Assert.Equal(
                SurfaceStyleDefaults.FontSize,
                nativeApi.LastInputBoxConfig?.FontSize);
            Assert.Equal(
                SurfaceStyleDefaults.FontSize,
                nativeApi.LastQuickActionsConfig?.FontSize);
            Assert.Equal(
                SurfaceStyleDefaults.BorderArgb,
                nativeApi.LastInputBoxConfig?.BorderColor);
            Assert.Equal(
                SurfaceStyleDefaults.BackgroundArgb,
                nativeApi.LastInputBoxConfig?.BackgroundColor);
            Assert.Equal(
                SurfaceStyleDefaults.ContentArgb,
                nativeApi.LastInputBoxConfig?.TextColor);
            Assert.Equal(
                SurfaceStyleDefaults.ContentArgb,
                nativeApi.LastInputBoxConfig?.CaretColor);
            Assert.Equal(
                SurfaceStyleDefaults.BorderArgb,
                nativeApi.LastQuickActionsConfig?.BorderColor);
            Assert.Equal(
                SurfaceStyleDefaults.BackgroundArgb,
                nativeApi.LastQuickActionsConfig?.BackgroundColor);
            Assert.Equal(
                SurfaceStyleDefaults.ContentArgb,
                nativeApi.LastQuickActionsConfig?.TextColor);
            Assert.Equal(
                SurfaceStyleDefaults.ContentArgb,
                nativeApi.LastQuickActionsConfig?.AccentColor);

            service.SynchronizeQuickActions(
            [
                new QuickActionSnapshot("alpha", "  Alpha\r\nquick action  "),
            ]);
            Assert.Equal(1, nativeApi.QuickActionItems.Count);
            Assert.Equal("Alpha  quick action", nativeApi.QuickActionItems[0].Label);
            var alphaToken = nativeApi.QuickActionItems[0].Token;

            var quickActionActivated = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.QuickActionActivated += quickActionActivated.SetResult;
            nativeApi.RaiseFeatureActivated(alphaToken);
            Assert.Equal(
                "alpha",
                await quickActionActivated.Task.WaitAsync(TimeSpan.FromSeconds(2)));

            var quickActionUnavailable = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.QuickActionUnavailable += () => quickActionUnavailable.SetResult(true);
            nativeApi.RaiseFeatureActivated(0);
            Assert.True(
                await quickActionUnavailable.Task.WaitAsync(TimeSpan.FromSeconds(2)),
                "Token zero must report an unavailable Quick Action.");

            nativeApi.SetFeatureItemsResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(
                () => service.SynchronizeQuickActions(
                [
                    new QuickActionSnapshot("beta", "Beta"),
                ]));
            var failedBetaToken = nativeApi.QuickActionItems[0].Token;

            var restoredQuickActionActivated = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.QuickActionActivated += restoredQuickActionActivated.SetResult;
            nativeApi.RaiseFeatureActivated(alphaToken);
            Assert.Equal(
                "alpha",
                await restoredQuickActionActivated.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            nativeApi.SetFeatureItemsResult = 0;
            service.SynchronizeQuickActions(
            [
                new QuickActionSnapshot("gamma", "Gamma"),
            ]);
            Assert.NotEqual(
                failedBetaToken,
                nativeApi.QuickActionItems[0].Token,
                "A token observed by a failed Native synchronization must not be reused.");

            var inputSubmitted = new TaskCompletionSource<InputSubmission>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.InputSubmitted += inputSubmitted.SetResult;
            nativeApi.RaiseInputSubmitted("hello native", InputMode.Ask);
            var submission = await inputSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("hello native", submission.Text);
            Assert.Equal(InputMode.Ask, submission.Mode);

            var inputChanged = new TaskCompletionSource<InputChanged>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.InputChanged += inputChanged.SetResult;
            nativeApi.RaiseInputChanged("bb", InputMode.General, revision: 42);
            var change = await inputChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("bb", change.Text);
            Assert.Equal(InputMode.General, change.Mode);
            Assert.Equal(42UL, change.Revision);

            var acceptedCandidates = service.SetInputCandidates(
            [
                new InputCandidate(
                    7,
                    CandidateKind.File,
                    CandidateIconKind.Document,
                    "bbb.md",
                    @"C:\aaa",
                    @"C:\aaa\bbb.md",
                    CandidateActions.Open | CandidateActions.Reveal | CandidateActions.CopyPath),
                new InputCandidate(
                    8,
                    CandidateKind.Command,
                    CandidateIconKind.Command,
                    "电源",
                    string.Empty),
            ], revision: 42);
            Assert.Equal(InputCandidateSetResult.Accepted, acceptedCandidates);
            Assert.Equal(42UL, nativeApi.InputCandidateRevision);
            Assert.Equal(2, nativeApi.InputCandidates.Count);
            Assert.Equal(7UL, nativeApi.InputCandidates[0].Token);
            Assert.Equal(CandidateKind.File, nativeApi.InputCandidates[0].Kind);
            Assert.Equal(CandidateIconKind.Document, nativeApi.InputCandidates[0].IconKind);
            Assert.Equal(
                CandidateActions.Open | CandidateActions.Reveal | CandidateActions.CopyPath,
                nativeApi.InputCandidates[0].Actions);
            Assert.Equal("bbb.md", nativeApi.InputCandidates[0].Primary);
            Assert.Equal(@"C:\aaa\bbb.md", nativeApi.InputCandidates[0].IconSource);
            Assert.Equal("电源", nativeApi.InputCandidates[1].Primary);
            Assert.True(nativeApi.InputCandidates[1].IconSource is null);
            Assert.True(
                nativeApi.InputCandidateTextPointersWerePacked,
                "Candidate text pointers must reference one contiguous UTF-16 payload.");

            service.ReplaceCommandInput("/luv ");
            Assert.Equal(1, nativeApi.ReplacedInputTexts.Count);
            Assert.Equal("/luv ", nativeApi.ReplacedInputTexts[0].Text);
            Assert.Equal("/luv ".Length, nativeApi.ReplacedInputTexts[0].Length);
            Assert.Equal((int)InputMode.Command, nativeApi.ReplacedInputTexts[0].InputMode);

            var longSecondaryText = new string(
                'x',
                InputCandidatePresentation.MaximumSecondaryTextLength - 1)
                + "\U0001F642-tail";
            var longTextCandidate = new InputCandidate(
                8,
                CandidateKind.File,
                CandidateIconKind.GenericFile,
                "long-path.txt",
                longSecondaryText,
                new string('i', InputCandidatePresentation.MaximumIconSourceLength - 1)
                    + "\U0001F642-tail");
            Assert.Equal(
                InputCandidateSetResult.Accepted,
                service.SetInputCandidates([longTextCandidate], revision: 43));
            Assert.Equal(
                InputCandidatePresentation.MaximumSecondaryTextLength - 1,
                nativeApi.InputCandidates[0].Secondary.Length,
                "Candidate presentation truncation split a UTF-16 surrogate pair.");
            Assert.Equal(
                InputCandidatePresentation.MaximumIconSourceLength - 1,
                nativeApi.InputCandidates[0].IconSource?.Length,
                "Candidate icon-source truncation split a UTF-16 surrogate pair.");
            Assert.True(
                ReferenceEquals(longSecondaryText, longTextCandidate.SecondaryText),
                "ABI presentation truncation must not replace the Core activation value.");

            nativeApi.SetInputCandidatesResult = 1;
            Assert.Equal(
                InputCandidateSetResult.Stale,
                service.SetInputCandidates([longTextCandidate], revision: 42));
            Assert.Equal(
                InputCandidateSetResult.Stale,
                service.SetInputCandidates([], revision: 42),
                "The empty candidate fast path must preserve stale-revision rejection.");
            nativeApi.SetInputCandidatesResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(
                () => service.SetInputCandidates([longTextCandidate], revision: 44));
            nativeApi.SetInputCandidatesResult = 0;

            var candidateActivated = new TaskCompletionSource<CandidateActivated>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.CandidateActivated += candidateActivated.SetResult;
            nativeApi.RaiseCandidateActivated(7, CandidateAction.Reveal);
            var activation = await candidateActivated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(7UL, activation.Token);
            Assert.Equal(CandidateAction.Reveal, activation.Action);

            service.ShowCommandInput();
            service.HideCommandInput();
            service.DismissCommandInput();
            service.ToggleQuickActions();
            service.EnqueueMessage("  hello queue  ");
            service.EnqueueMessage("   ");

            var messageActivity = service.BeginMessageActivity("  Indexing files  ");
            Assert.Equal(1, nativeApi.BegunMessageActivities.Count);
            var messageActivityToken = nativeApi.BegunMessageActivities[0].Token;
            Assert.NotEqual(0UL, messageActivityToken);
            Assert.Equal("Indexing files", nativeApi.BegunMessageActivities[0].Text);
            messageActivity.Update("  Indexed 10 files  ");
            Assert.Equal(1, nativeApi.UpdatedMessageActivities.Count);
            Assert.Equal(messageActivityToken, nativeApi.UpdatedMessageActivities[0].Token);
            Assert.Equal("Indexed 10 files", nativeApi.UpdatedMessageActivities[0].Text);
            messageActivity.Complete("  Index ready  ");
            messageActivity.Complete("ignored duplicate completion");
            Assert.Equal(1, nativeApi.CompletedMessageActivities.Count);
            Assert.Equal(messageActivityToken, nativeApi.CompletedMessageActivities[0].Token);
            Assert.Equal("Index ready", nativeApi.CompletedMessageActivities[0].Text);
            Assert.Throws<ObjectDisposedException>(() => messageActivity.Update("too late"));

            var disposedActivity = service.BeginMessageActivity("Waiting");
            var disposedToken = nativeApi.BegunMessageActivities[^1].Token;
            Assert.NotEqual(messageActivityToken, disposedToken);
            disposedActivity.Dispose();
            disposedActivity.Dispose();
            Assert.Equal(2, nativeApi.CompletedMessageActivities.Count);
            Assert.Equal(disposedToken, nativeApi.CompletedMessageActivities[^1].Token);
            Assert.True(nativeApi.CompletedMessageActivities[^1].Text is null);
            Assert.Equal(0, nativeApi.CompletedMessageActivities[^1].Length);
            Assert.Throws<ArgumentException>(() => service.BeginMessageActivity("   "));

            service.ToggleMessageQueue();
            service.HideMessageQueue();
            service.HidePopups();
            Assert.Equal(1, nativeApi.ShowInputBoxCalls);
            Assert.Equal(1, nativeApi.HideInputBoxCalls);
            Assert.Equal(1, nativeApi.DismissInputBoxCalls);
            Assert.Equal(1, nativeApi.ToggleQuickActionsCalls);
            Assert.Equal(1, nativeApi.EnqueuedMessages.Count);
            Assert.Equal("hello queue", nativeApi.EnqueuedMessages[0].Text);
            Assert.Equal("hello queue".Length, nativeApi.EnqueuedMessages[0].Length);
            Assert.Equal(1, nativeApi.ToggleMessageQueueCalls);
            Assert.Equal(1, nativeApi.HideMessageQueueCalls);
            Assert.Equal(1, nativeApi.HidePopupsCalls);
            Assert.Equal(0, nativeApi.HideQuickActionsCalls);

            nativeApi.ToggleInputBoxResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(service.ToggleCommandInput);
            nativeApi.HidePopupsResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(service.HidePopups);
            nativeApi.EnqueueMessageResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(() => service.EnqueueMessage("failure"));
            nativeApi.BeginMessageActivityResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(() => service.BeginMessageActivity("failure"));
            nativeApi.BeginMessageActivityResult = 0;
            var failingActivity = service.BeginMessageActivity("starting");
            nativeApi.UpdateMessageActivityResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(() => failingActivity.Update("failure"));
            nativeApi.UpdateMessageActivityResult = 0;
            nativeApi.CompleteMessageActivityResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(() => failingActivity.Complete("failure"));
            nativeApi.CompleteMessageActivityResult = 0;
            failingActivity.Complete();
            nativeApi.ToggleMessageQueueResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(service.ToggleMessageQueue);
            nativeApi.HideMessageQueueResult = unchecked((int)0x80004005);
            Assert.Throws<ExternalException>(service.HideMessageQueue);
        }
        finally
        {
            service.Dispose();
        }

        Assert.Equal(1, nativeApi.ShutdownCalls);
        Assert.True(nativeApi.InputSubmittedCallback is null);
        Assert.True(nativeApi.InputChangedCallback is null);
        Assert.True(nativeApi.CandidateActivatedCallback is null);
        Assert.True(nativeApi.QuickActionActivatedCallback is null);
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
