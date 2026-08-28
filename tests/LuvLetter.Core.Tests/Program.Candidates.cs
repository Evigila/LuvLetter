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
        Assert.True(commands.Register("build", _ => commandInvoked.Set()));
        Assert.True(commands.Register("beta", _ => { }));
        Assert.True(commands.Register("zeta", _ => { }));

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

            fileIndex.SetState(new FileIndexRuntimeState(
                FileIndexRuntimeActivity.Updating,
                1));
            Assert.SequenceEqual(
                ["正在更新索引"],
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
            Assert.Equal(6, general.Count);
            Assert.SequenceEqual(
                [
                    CandidateKind.File,
                    CandidateKind.File,
                    CandidateKind.File,
                    CandidateKind.File,
                    CandidateKind.Command,
                    CandidateKind.GlobalSearch,
                ],
                general.Select(static item => item.Kind));
            Assert.Equal("beta", general[4].PrimaryText);
            Assert.SequenceEqual(
                [
                    CandidateIconKind.Document,
                    CandidateIconKind.Folder,
                    CandidateIconKind.Image,
                    CandidateIconKind.Archive,
                    CandidateIconKind.Command,
                    CandidateIconKind.Search,
                ],
                general.Select(static item => item.IconKind));
            Assert.Equal(@"C:\aaa", general[0].SecondaryText);
            Assert.Equal(@"C:\logs", general[1].SecondaryText);
            Assert.Equal(5, fileIndex.Queries.Single().MaximumResults);

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

            nativeShell.RaiseInputChanged("b", InputMode.Command, revision: 3);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 3),
                    TimeSpan.FromSeconds(2)),
                "Command input did not produce command candidates.");
            var commandOnly = nativeShell.CandidateSnapshots.Last(item => item.Revision == 3).Candidates;
            Assert.SequenceEqual(
                [CandidateKind.Command, CandidateKind.Command],
                commandOnly.Select(static item => item.Kind));
            Assert.SequenceEqual(
                ["beta", "build"],
                commandOnly.Select(static item => item.PrimaryText));
            Assert.Equal(1, fileIndex.Queries.Count);

            var delayed = new TaskCompletionSource<IReadOnlyList<FileIndexMatch>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            fileIndex.SetQuery((_, _, _, _) => new ValueTask<IReadOnlyList<FileIndexMatch>>(delayed.Task));
            nativeShell.RaiseInputChanged("old", InputMode.General, revision: 4);
            Assert.True(
                SpinWait.SpinUntil(
                    () => fileIndex.Queries.Any(item => item.Revision == 4),
                    TimeSpan.FromSeconds(2)),
                "The delayed revision did not start querying.");
            nativeShell.RaiseInputChanged("new", InputMode.Ask, revision: 5);
            delayed.SetResult(
                [new(9, FileSystemEntryKind.File, "old.txt", @"C:\old.txt")]);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 5),
                    TimeSpan.FromSeconds(2)),
                "The latest revision was not published.");
            Assert.False(
                nativeShell.CandidateSnapshots.Any(item => item.Revision == 4),
                "A stale file query overwrote the latest editor revision.");

            fileIndex.SetQuery(static (_, _, _, _) =>
                ValueTask.FromResult<IReadOnlyList<FileIndexMatch>>(
                    [
                        new(10, FileSystemEntryKind.File, "bbb.md", @"C:\aaa\bbb.md"),
                        new(11, FileSystemEntryKind.Directory, "builds", @"C:\logs\builds"),
                    ]));
            nativeShell.RaiseInputChanged("b", InputMode.General, revision: 6);
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Any(item => item.Revision == 6),
                    TimeSpan.FromSeconds(2)),
                "Activation candidates were not published.");
            var revisionSixQueryCount = fileIndex.Queries.Count(
                static item => item.Revision == 6);
            var initialRevisionSixCandidates = nativeShell.CandidateSnapshots
                .Last(item => item.Revision == 6).Candidates;
            fileIndex.RaiseIndexChanged();
            Assert.True(
                SpinWait.SpinUntil(
                    () => fileIndex.Queries.Count(item => item.Revision == 6)
                        > revisionSixQueryCount,
                    TimeSpan.FromSeconds(2)),
                "An index generation change did not requery the current editor revision.");
            Assert.True(
                SpinWait.SpinUntil(
                    () => nativeShell.CandidateSnapshots.Count(item => item.Revision == 6) >= 2,
                    TimeSpan.FromSeconds(2)),
                "The refreshed index results were not republished.");
            var activationCandidates = nativeShell.CandidateSnapshots
                .Last(item => item.Revision == 6).Candidates;
            var fileCandidate = activationCandidates.First(item => item.Kind == CandidateKind.File);
            var directoryCandidate = activationCandidates.First(
                item => item.Kind == CandidateKind.File && item.PrimaryText == "builds");
            var commandCandidate = activationCandidates.First(
                item => item.Kind == CandidateKind.Command && item.PrimaryText == "build");
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
            Assert.Equal(1, nativeShell.HideCommandInputCalls);

            nativeShell.RaiseCandidateActivated(directoryCandidate.Token, CandidateAction.Open);
            Assert.SequenceEqual([@"C:\logs\builds"], launcher.Opened);
            Assert.SequenceEqual([FileSystemEntryKind.Directory], launcher.OpenedKinds);
            Assert.Equal(2, nativeShell.HideCommandInputCalls);

            nativeShell.RaiseCandidateActivated(directoryCandidate.Token, CandidateAction.Reveal);
            Assert.SequenceEqual(
                [@"C:\aaa\bbb.md", @"C:\logs\builds"],
                launcher.Revealed);
            Assert.SequenceEqual(
                [FileSystemEntryKind.File, FileSystemEntryKind.Directory],
                launcher.RevealedKinds);
            Assert.Equal(3, nativeShell.HideCommandInputCalls);

            nativeShell.RaiseCandidateActivated(commandCandidate.Token, CandidateAction.Open);
            Assert.True(
                commandInvoked.Wait(TimeSpan.FromSeconds(2)),
                "The selected command candidate did not dispatch.");
            Assert.Equal(4, nativeShell.HideCommandInputCalls);

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
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }
}
