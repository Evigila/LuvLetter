using LuvLetter.Core.Application;
using LuvLetter.Core.Commands;
using LuvLetter.Core.NativeShell;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static async Task TestInputCandidates()
    {
        Assert.Equal(
            CandidateIconKind.GenericFile,
            CandidateIconClassifier.Classify(FileSystemEntryKind.File, @"C:\data\item.unknown"));
        Assert.Equal(
            CandidateIconKind.Audio,
            CandidateIconClassifier.Classify(FileSystemEntryKind.File, @"C:\media\song.flac"));
        Assert.Equal(
            CandidateIconKind.Video,
            CandidateIconClassifier.Classify(FileSystemEntryKind.File, @"C:\media\movie.mkv"));
        Assert.Equal(
            CandidateIconKind.Executable,
            CandidateIconClassifier.Classify(FileSystemEntryKind.File, @"C:\tools\run.exe"));

        using var commands = new CommandDispatcher();
        using var commandInvoked = new ManualResetEventSlim();
        Assert.True(commands.Register("luv", "build", _ => commandInvoked.Set()));
        Assert.True(commands.Register("luv", "beta", _ => { }));
        Assert.True(commands.Register("luv", "index refresh", _ => { }));
        Assert.True(commands.RegisterLink("luv", "refreshindex", "luv", "index refresh"));
        Assert.True(commands.Register("luv", "zeta", _ => { }));

        var nativeShell = new FakeNativeShell();
        var fileIndex = new FakeFileIndexClient();
        var launcher = new FakeFileCandidateLauncher();
        fileIndex.SetQuery(static (_, _, _, _) =>
            ValueTask.FromResult<IReadOnlyList<FileIndexMatch>>(
            [
                new(1, FileSystemEntryKind.File, "bbb.md", @"C:\aaa\bbb.md"),
                new(2, FileSystemEntryKind.Directory, "builds", @"C:\logs\builds"),
                new(3, FileSystemEntryKind.File, "banner.PNG", @"C:\images\banner.PNG"),
                new(4, FileSystemEntryKind.File, "bundle.zip", @"C:\packages\bundle.zip"),
            ]));
        fileIndex.SetState(new FileIndexRuntimeState(
            FileIndexRuntimeActivity.InitialBuild,
            0));

        using var coordinator = new InputCandidateCoordinator(
            nativeShell,
            fileIndex,
            launcher,
            commands,
            new InputCandidateOptions());
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            Assert.SequenceEqual(
                ["正在生成索引表"],
                nativeShell.BegunMessageActivities);

            var progressState = new FileIndexRuntimeState(
                FileIndexRuntimeActivity.InitialBuild,
                0,
                FileIndexRuntimeStage.Scanning,
                37,
                ProgressIsEstimated: true,
                DiscoveredEntries: 12);
            fileIndex.SetState(progressState);
            Assert.SequenceEqual(
                ["正在生成索引表 · 扫描中 · 约37% · 12 项"],
                nativeShell.UpdatedMessageActivities);
            fileIndex.SetState(progressState);
            Assert.Equal(
                1,
                nativeShell.UpdatedMessageActivities.Count,
                "An unchanged index progress state must not repaint the activity.");

            fileIndex.SetState(new FileIndexRuntimeState(
                FileIndexRuntimeActivity.Updating,
                1));
            Assert.SequenceEqual(
                [
                    "正在生成索引表 · 扫描中 · 约37% · 12 项",
                    "正在更新索引",
                ],
                nativeShell.UpdatedMessageActivities);

            fileIndex.SetState(new FileIndexRuntimeState(
                FileIndexRuntimeActivity.Ready,
                2));
            Assert.SequenceEqual(
                ["索引已就绪"],
                nativeShell.CompletedMessageActivities);
            fileIndex.SetState(new FileIndexRuntimeState(
                FileIndexRuntimeActivity.Ready,
                3));
            Assert.Equal(
                1,
                nativeShell.CompletedMessageActivities.Count,
                "A stable generation change must not repeat the ready message.");

            nativeShell.RaiseInputChanged("b", InputMode.General, revision: 1);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 1),
                    TimeSpan.FromSeconds(2)),
                "General input did not produce candidates.");
            var general = nativeShell.CandidateSnapshots.Last(item => item.Revision == 1).Candidates;
            Assert.Equal(5, general.Count);
            Assert.SequenceEqual(
                [
                    CandidateKind.File,
                    CandidateKind.File,
                    CandidateKind.File,
                    CandidateKind.File,
                    CandidateKind.GlobalSearch,
                ],
                general.Select(static item => item.Kind));
            Assert.SequenceEqual(
                [
                    CandidateIconKind.Document,
                    CandidateIconKind.Folder,
                    CandidateIconKind.Image,
                    CandidateIconKind.Archive,
                    CandidateIconKind.Search,
                ],
                general.Select(static item => item.IconKind));
            Assert.Equal(@"C:\aaa", general[0].SecondaryText);
            Assert.Equal(@"C:\logs", general[1].SecondaryText);
            Assert.Equal(64, fileIndex.Queries.Single().MaximumResults);

            nativeShell.RaiseInputChanged("b", InputMode.Ask, revision: 2);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 2),
                    TimeSpan.FromSeconds(2)),
                "Ask input did not clear candidates.");
            Assert.Equal(
                0,
                nativeShell.CandidateSnapshots.Last(item => item.Revision == 2).Candidates.Count);
            Assert.Equal(1, fileIndex.Queries.Count);

            nativeShell.RaiseInputChanged(string.Empty, InputMode.Command, revision: 3);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 3),
                    TimeSpan.FromSeconds(2)),
                "Empty Command input did not produce command-domain candidates.");
            var domains = nativeShell.CandidateSnapshots.Last(item => item.Revision == 3).Candidates;
            Assert.SequenceEqual(["luv"], domains.Select(static item => item.PrimaryText));
            Assert.Equal(CandidateActions.Complete, domains.Single().Actions);
            nativeShell.RaiseCandidateActivated(
                domains.Single().Token,
                CandidateAction.Complete);
            Assert.SequenceEqual(["/luv "], nativeShell.ReplacedCommandInputs);
            Assert.Equal(0, nativeShell.HideCommandInputCalls);

            nativeShell.RaiseInputChanged("/luv ", InputMode.Command, revision: 4);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 4),
                    TimeSpan.FromSeconds(2)),
                "A completed domain did not produce its first command-path segments.");
            var commandRoots = nativeShell.CandidateSnapshots.Last(item => item.Revision == 4).Candidates;
            Assert.SequenceEqual(
                ["beta", "build", "index", "refreshindex", "zeta"],
                commandRoots.Select(static item => item.PrimaryText));
            var index = commandRoots.Single(item => item.PrimaryText == "index");
            Assert.Equal(CandidateActions.Complete, index.Actions);
            Assert.True(commandRoots.Single(item => item.PrimaryText == "refreshindex")
                .SecondaryText.StartsWith("Link", StringComparison.Ordinal));
            nativeShell.RaiseCandidateActivated(index.Token, CandidateAction.Complete);
            Assert.SequenceEqual(["/luv ", "/luv index "], nativeShell.ReplacedCommandInputs);

            nativeShell.RaiseInputChanged("/luv index ", InputMode.Command, revision: 5);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 5),
                    TimeSpan.FromSeconds(2)),
                "A command-path branch did not produce its child segment.");
            var refresh = nativeShell.CandidateSnapshots.Last(item => item.Revision == 5)
                .Candidates.Single();
            Assert.Equal("refresh", refresh.PrimaryText);
            Assert.Equal(
                CandidateActions.Open | CandidateActions.Complete,
                refresh.Actions);
            nativeShell.RaiseCandidateActivated(refresh.Token, CandidateAction.Complete);
            Assert.SequenceEqual(
                ["/luv ", "/luv index ", "/luv index refresh "],
                nativeShell.ReplacedCommandInputs);

            nativeShell.RaiseInputChanged("/luv b", InputMode.Command, revision: 6);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 6),
                    TimeSpan.FromSeconds(2)),
                "A registered command domain did not produce child commands.");
            var commandOnly = nativeShell.CandidateSnapshots.Last(item => item.Revision == 6).Candidates;
            Assert.SequenceEqual(
                [CandidateKind.Command, CandidateKind.Command],
                commandOnly.Select(static item => item.Kind));
            Assert.SequenceEqual(
                ["beta", "build"],
                commandOnly.Select(static item => item.PrimaryText));
            Assert.True(commandOnly.All(item => item.Actions
                == (CandidateActions.Open | CandidateActions.Complete)));
            Assert.Equal(1, fileIndex.Queries.Count);
            var build = commandOnly.Single(item => item.PrimaryText == "build");
            nativeShell.RaiseCandidateActivated(build.Token, CandidateAction.Complete);
            Assert.SequenceEqual(
                ["/luv ", "/luv index ", "/luv index refresh ", "/luv build "],
                nativeShell.ReplacedCommandInputs);
            Assert.False(commandInvoked.IsSet, "Tab completion must not execute a command.");
            Assert.Equal(0, nativeShell.HideCommandInputCalls);
            nativeShell.RaiseCandidateActivated(build.Token, CandidateAction.Open);
            Assert.True(
                commandInvoked.Wait(TimeSpan.FromSeconds(2)),
                "The selected scoped command candidate did not dispatch.");
            Assert.Equal(1, nativeShell.HideCommandInputCalls);

            var delayed = new TaskCompletionSource<IReadOnlyList<FileIndexMatch>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            fileIndex.SetQuery((_, _, _, _) => new ValueTask<IReadOnlyList<FileIndexMatch>>(delayed.Task));
            nativeShell.RaiseInputChanged("old", InputMode.General, revision: 7);
            Assert.True(
                SpinWait.SpinUntil(
                    () => fileIndex.Queries.Any(item => item.Revision == 7),
                    TimeSpan.FromSeconds(2)),
                "The delayed revision did not start querying.");
            nativeShell.RaiseInputChanged("new", InputMode.Ask, revision: 8);
            delayed.SetResult(
                [new(9, FileSystemEntryKind.File, "old.txt", @"C:\old.txt")]);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 8),
                    TimeSpan.FromSeconds(2)),
                "The latest revision was not published.");
            Assert.False(
                nativeShell.CandidateSnapshots.Any(item => item.Revision == 7),
                "A stale file query overwrote the latest editor revision.");

            fileIndex.SetQuery(static (_, _, _, _) =>
                ValueTask.FromResult<IReadOnlyList<FileIndexMatch>>(
                    [
                        new(10, FileSystemEntryKind.File, "bbb.md", @"C:\aaa\bbb.md"),
                        new(11, FileSystemEntryKind.Directory, "builds", @"C:\logs\builds"),
                    ]));
            // A changed source must invalidate the same-query cache before the new edit.
            fileIndex.RaiseIndexChanged();
            nativeShell.RaiseInputChanged("b", InputMode.General, revision: 9);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 9),
                    TimeSpan.FromSeconds(2)),
                "Activation candidates were not published.");
            var currentRevisionQueryCount = fileIndex.Queries.Count(
                static item => item.Revision == 9);
            var initialRevisionSixCandidates = nativeShell.CandidateSnapshots
                .Last(item => item.Revision == 9).Candidates;
            fileIndex.RaiseIndexChanged();
            Assert.True(
                SpinWait.SpinUntil(
                    () => fileIndex.Queries.Count(item => item.Revision == 9)
                        > currentRevisionQueryCount,
                    TimeSpan.FromSeconds(2)),
                "An index generation change did not requery the current editor revision.");
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Count(item => item.Revision == 9) >= 2,
                    TimeSpan.FromSeconds(2)),
                "The refreshed index results were not republished.");
            var activationCandidates = nativeShell.CandidateSnapshots
                .Last(item => item.Revision == 9).Candidates;
            var fileCandidate = activationCandidates.First(item => item.Kind == CandidateKind.File);
            var directoryCandidate = activationCandidates.First(
                item => item.Kind == CandidateKind.File && item.PrimaryText == "builds");
            var globalCandidate = activationCandidates.First(
                item => item.Kind == CandidateKind.GlobalSearch);

            foreach (var refreshed in activationCandidates)
            {
                var original = initialRevisionSixCandidates.Single(
                    item => item.Kind == refreshed.Kind
                        && item.PrimaryText == refreshed.PrimaryText);
                Assert.Equal(
                    original.Token,
                    refreshed.Token,
                    "An unchanged candidate must preserve its token during a same-revision refresh.");
            }

            nativeShell.RaiseCandidateActivated(fileCandidate.Token, CandidateAction.Reveal);
            Assert.SequenceEqual([@"C:\aaa\bbb.md"], launcher.Revealed);
            Assert.SequenceEqual([FileSystemEntryKind.File], launcher.RevealedKinds);
            Assert.Equal(2, nativeShell.HideCommandInputCalls);
            Assert.Equal(
                0,
                nativeShell.EnqueuedMessages.Count,
                "Successful candidate activation must not report a stale indexed item.");

            nativeShell.RaiseCandidateActivated(directoryCandidate.Token, CandidateAction.Open);
            Assert.SequenceEqual([@"C:\logs\builds"], launcher.Opened);
            Assert.SequenceEqual([FileSystemEntryKind.Directory], launcher.OpenedKinds);
            Assert.Equal(3, nativeShell.HideCommandInputCalls);

            nativeShell.RaiseCandidateActivated(directoryCandidate.Token, CandidateAction.Reveal);
            Assert.SequenceEqual(
                [@"C:\aaa\bbb.md", @"C:\logs\builds"],
                launcher.Revealed);
            Assert.SequenceEqual(
                [FileSystemEntryKind.File, FileSystemEntryKind.Directory],
                launcher.RevealedKinds);
            Assert.Equal(4, nativeShell.HideCommandInputCalls);

            launcher.OpenResult = false;
            nativeShell.RaiseCandidateActivated(directoryCandidate.Token, CandidateAction.Open);
            Assert.Equal(
                4,
                nativeShell.HideCommandInputCalls,
                "A missing indexed item must keep the input window open.");
            Assert.Equal(
                @"The indexed item is no longer available: C:\logs\builds",
                nativeShell.EnqueuedMessages[^1]);
            launcher.OpenResult = true;

            nativeShell.RaiseCandidateActivated(globalCandidate.Token, CandidateAction.Open);
            Assert.Equal("全局搜索功能尚未实现。", nativeShell.EnqueuedMessages[^1]);
            Assert.Equal(
                4,
                nativeShell.HideCommandInputCalls,
                "The Global Search placeholder must keep the input window open.");

            var hideCountBeforeEnter = nativeShell.HideCommandInputCalls;
            nativeShell.RaiseInputSubmitted("unselected text", InputMode.General);
            Assert.Equal(
                hideCountBeforeEnter,
                nativeShell.HideCommandInputCalls,
                "An ordinary submission must not activate an unselected candidate.");

            var longParentPath = @"C:\" + new string(
                'p',
                InputCandidatePresentation.MaximumSecondaryTextLength - 4)
                + "\U0001F642";
            var longFullPath = longParentPath + @"\deep.txt";
            fileIndex.SetQuery((_, _, _, _) =>
                ValueTask.FromResult<IReadOnlyList<FileIndexMatch>>(
                [
                    new(50, FileSystemEntryKind.File, "deep.txt", longFullPath),
                ]));
            nativeShell.SetInputCandidatesResult = InputCandidateSetResult.Stale;
            var currentRevisionSnapshotCount = nativeShell.CandidateSnapshots.Count(
                static item => item.Revision == 10);
            nativeShell.RaiseInputChanged("deep", InputMode.General, revision: 10);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Count(item => item.Revision == 10)
                        > currentRevisionSnapshotCount,
                    TimeSpan.FromSeconds(2)),
                "The stale Native candidate update was not attempted.");
            var rejectedCandidate = nativeShell.CandidateSnapshots
                .Last(item => item.Revision == 10)
                .Candidates[0];
            var openedBeforeRejectedActivation = launcher.Opened.Count;
            nativeShell.RaiseCandidateActivated(rejectedCandidate.Token);
            Assert.Equal(
                openedBeforeRejectedActivation,
                launcher.Opened.Count,
                "A Native-rejected candidate snapshot published its activation target.");

            nativeShell.SetInputCandidatesResult = InputCandidateSetResult.Accepted;
            fileIndex.RaiseIndexChanged();
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Count(item => item.Revision == 10)
                        >= currentRevisionSnapshotCount + 2,
                    TimeSpan.FromSeconds(2)),
                "The current revision was not republished after a stale Native rejection.");
            var acceptedCandidate = nativeShell.CandidateSnapshots
                .Last(item => item.Revision == 10)
                .Candidates[0];
            Assert.NotEqual(
                rejectedCandidate.Token,
                acceptedCandidate.Token,
                "A token from a Native-rejected snapshot was committed for reuse.");
            Assert.Equal(
                InputCandidatePresentation.MaximumSecondaryTextLength - 1,
                acceptedCandidate.SecondaryText.Length,
                "The long parent path was not safely truncated for Native presentation.");
            Assert.False(
                char.IsSurrogate(acceptedCandidate.SecondaryText[^1]),
                "The long parent path presentation ends with a split surrogate pair.");

            nativeShell.RaiseCandidateActivated(acceptedCandidate.Token);
            Assert.Equal(
                longFullPath,
                launcher.Opened[^1],
                "Presentation truncation changed the complete Core activation path.");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }
}
