using System.Collections.Concurrent;
using LuvLetter.Core.Application;
using LuvLetter.Core.Commands;
using LuvLetter.Core.NativeShell;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestApplicationNameMatching()
    {
        var todo = TestApplication("todo", "Microsoft To Do", ["todo.exe", "Tasks"]);
        var exact = ApplicationNameMatcher.Score(todo, "Microsoft To Do");
        Assert.True(exact.HasValue);
        Assert.Equal(exact, ApplicationNameMatcher.Score(todo, "mICROSOFT tO dO"));
        Assert.Equal(exact, ApplicationNameMatcher.Score(todo, "  Microsoft To Do  "));
        var alias = ApplicationNameMatcher.Score(todo, "TODO.EXE");
        var compact = ApplicationNameMatcher.Score(todo, "Microsoft todo");
        var prefix = ApplicationNameMatcher.Score(todo, "Micro");
        Assert.True(alias.HasValue && compact.HasValue && prefix.HasValue);
        Assert.True(exact > alias && alias > compact && compact > prefix,
            "Raw exact names, exact aliases, compact names, and prefixes must retain their ranking tiers.");
        Assert.True(ApplicationNameMatcher.Score(todo, "tasks").HasValue);
        Assert.True(ApplicationNameMatcher.Score(todo, "Microsoftto").HasValue);
        var indexed = ApplicationNameMatcher.CreateIndex(todo);
        foreach (var query in new[] { "Microsoft To Do", "TODO.EXE", "Microsoft todo", "Micro", "tasks" })
        {
            var preparedQuery = ApplicationNameMatcher.CreateQuery(query);
            Assert.Equal(ApplicationNameMatcher.Score(todo, query),
                ApplicationNameMatcher.Score(todo, indexed, preparedQuery),
                "Precomputed application names must preserve matching scores.");
        }
        var localized = TestApplication("power", "Power Options", ["电源选项"]);
        Assert.Equal(ApplicationNameMatcher.Score(localized, "电源"), ApplicationNameMatcher.Score(localized,
            ApplicationNameMatcher.CreateIndex(localized), ApplicationNameMatcher.CreateQuery("电源")),
            "Precomputed localized aliases must preserve prefix matching.");
        foreach (var query in new[] { "", "   ", @"C:\Apps\todo", "Apps/todo", "Microsoft/to", "Microsofft", "Microsoft-To-Do" })
        {
            Assert.False(ApplicationNameMatcher.Score(todo, query).HasValue,
                $"Application matching must not invent path, fuzzy, or punctuation matching for '{query}'.");
        }
        var sameName = todo with { Id = "todo-other", LaunchTarget = @"C:\Other\todo.exe" };
        Assert.Equal(compact, ApplicationNameMatcher.Score(sameName, "Microsoft todo"));
        Assert.NotEqual(todo.Id, sameName.Id,
            "Matching eligibility must preserve distinct launch identities with the same display name.");

        var file = new FileIndexMatch(1, FileSystemEntryKind.File, "Microsoft To Do.txt", @"C:\Docs\Microsoft To Do.txt");
        Assert.Equal(0, DefaultCandidateRankingPolicy.FileMatchScore(file, "Microsoft todo"),
            "Application whitespace compaction must not leak into filename matching.");
        Assert.True(DefaultCandidateRankingPolicy.FileMatchScore(file, "Microsoft To Do") >
            DefaultCandidateRankingPolicy.FileMatchScore(file, "Micro"));
        Assert.Equal(0, DefaultCandidateRankingPolicy.FileMatchScore(file, @"C:\Docs\Microsoft To Do"));
        return Task.CompletedTask;
    }

    private static Task TestApplicationCandidateRanking()
    {
        var policy = new DefaultCandidateRankingPolicy();
        var app = new CandidateRankingContext("app:todo", "micro", SearchCandidateSource.Application,
            ApplicationNameMatcher.Score(TestApplication("todo", "Microsoft To Do"), "micro")!.Value);
        var exactFile = new CandidateRankingContext("file:frequent", "micro", SearchCandidateSource.File, 300);
        var exactDirectory = exactFile with { Identity = "directory:micro", Source = SearchCandidateSource.Directory };
        Assert.True(policy.Score(app) > policy.Score(exactFile) && policy.Score(app) > policy.Score(exactDirectory),
            "Every eligible application must outrank even exact filesystem matches by default.");
        var boosted = new DefaultCandidateRankingPolicy(priorityProvider: new TestCandidatePriorityProvider(
            candidate => candidate.Identity == "file:frequent" ? 2000 : 0));
        Assert.True(boosted.Score(exactFile) > boosted.Score(app),
            "An injected file priority must override the default application bias.");
        Assert.Equal(policy.Score(exactDirectory), boosted.Score(exactDirectory),
            "A targeted priority must not alter unrelated candidates.");
        var noTypeBias = new DefaultCandidateRankingPolicy(new CandidateRankingOptions { ApplicationBias = 0 });
        Assert.True(noTypeBias.Score(exactFile) > noTypeBias.Score(app));
        return Task.CompletedTask;
    }

    private static async Task TestApplicationCandidateMerge()
    {
        using var commands = new CommandDispatcher();
        Assert.True(commands.Register("tool", "micro.command", _ => { }));
        var shell = new FakeNativeShell();
        var files = ApplicationTestFileIndex();
        var fileLauncher = new FakeFileCandidateLauncher();
        var apps = new TestApplicationCatalog(
            TestApplication("todo-a", "Microsoft To Do"),
            TestApplication("todo-b", "Microsoft To Do"));
        var launcher = new TestApplicationLauncher();
        using var coordinator = new InputCandidateCoordinator(shell, files, fileLauncher, commands,
            new InputCandidateOptions(), apps, launcher);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            shell.RaiseInputChanged("micro", revision: 1);
            var first = await WaitApplicationSnapshotAsync(shell, 1,
                predicate: candidates => candidates.Any(candidate => candidate.PrimaryText == "micro"));
            Assert.SequenceEqual(["Microsoft To Do", "Microsoft To Do", "micro"],
                first.Take(3).Select(candidate => candidate.PrimaryText));
            Assert.True(first.Take(2).All(candidate => candidate.Kind == CandidateKind.File &&
                candidate.IconKind == CandidateIconKind.Executable));
            Assert.NotEqual(first[0].Token, first[1].Token,
                "Distinct app identities must survive merge even when their names are identical.");
            Assert.Equal(CandidateKind.GlobalSearch, first[^1].Kind);

            var priorQueries = apps.QueryCount;
            var fileQueriesBeforeApplicationRefresh = files.Queries.Count;
            var priorSnapshots = shell.CandidateSnapshots.Count;
            apps.RaiseChanged();
            var refreshed = await WaitApplicationSnapshotAsync(shell, 1, priorSnapshots,
                candidates => candidates.Any(candidate => candidate.PrimaryText == "micro"));
            Assert.True(apps.QueryCount > priorQueries);
            Assert.Equal(fileQueriesBeforeApplicationRefresh, files.Queries.Count,
                "An application-only publication must reuse the unchanged file result.");
            Assert.SequenceEqual(first.Select(candidate => candidate.Token), refreshed.Select(candidate => candidate.Token),
                "An unchanged application catalog refresh must preserve same-revision identities and tokens.");

            var appQueriesBeforeFileRefresh = apps.QueryCount;
            var fileQueriesBeforeFileRefresh = files.Queries.Count;
            priorSnapshots = shell.CandidateSnapshots.Count;
            files.RaiseIndexChanged();
            refreshed = await WaitApplicationSnapshotAsync(shell, 1, priorSnapshots,
                candidates => candidates.Any(candidate => candidate.PrimaryText == "micro"));
            Assert.Equal(appQueriesBeforeFileRefresh, apps.QueryCount,
                "A file-only publication must reuse the unchanged application result.");
            Assert.True(files.Queries.Count > fileQueriesBeforeFileRefresh);

            shell.RaiseCandidateActivated(refreshed[0].Token);
            await WaitApplicationConditionAsync(() => launcher.Opened.Count == 1 && shell.HideCommandInputCalls == 1,
                "A successful app launch was not dispatched and dismissed.");
            Assert.Equal("todo-a", launcher.Opened.Single().Id);
            Assert.Empty(fileLauncher.Opened, "App rows must dispatch app identities instead of launching their file-shaped UI row.");
            shell.RaiseCandidateActivated(refreshed[1].Token, CandidateAction.Reveal);
            await WaitApplicationConditionAsync(() => launcher.Revealed.Count == 1 && shell.HideCommandInputCalls == 2,
                "Application reveal did not reach the application launcher.");
            Assert.Equal("todo-b", launcher.Revealed.Single().Id);

            launcher.Result = new ApplicationLaunchResult(false, "Application is unavailable.");
            shell.RaiseCandidateActivated(refreshed[0].Token);
            await WaitApplicationConditionAsync(() => shell.EnqueuedMessages.Contains("Application is unavailable."),
                "Application launch failure was not reported.");
            Assert.Equal(2, shell.HideCommandInputCalls, "A failed app launch must keep the input open.");

            priorSnapshots = shell.CandidateSnapshots.Count;
            apps.SetEntries([]);
            apps.RaiseChanged();
            var withoutApps = await WaitApplicationSnapshotAsync(shell, 1, priorSnapshots);
            Assert.False(withoutApps.Any(candidate => candidate.PrimaryText == "Microsoft To Do"));
            var launches = launcher.Opened.Count;
            shell.RaiseCandidateActivated(refreshed[0].Token);
            Assert.Equal(launches, launcher.Opened.Count, "Removed application tokens must not activate after catalog refresh.");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task TestApplicationCandidateIconSources()
    {
        using var commands = new CommandDispatcher();
        var shell = new FakeNativeShell();
        var files = new FakeFileIndexClient();
        files.SetQuery(static (_, _, _, _) =>
            ValueTask.FromResult<IReadOnlyList<FileIndexMatch>>([]));
        var apps = new TestApplicationCatalog(
            new("01", "Icon Shortcut", [], ApplicationLaunchKind.Shortcut,
                @"C:\Menu\Shortcut.lnk", ExecutablePath: @"C:\Apps\Shortcut.exe"),
            new("02", "Icon Executable", [], ApplicationLaunchKind.Executable,
                @"C:\Apps\Executable.exe", ExecutablePath: @"C:\Apps\Executable.exe"),
            new("03", "Icon Registered", [], ApplicationLaunchKind.RegisteredExecutable,
                "Registered.exe", ExecutablePath: @"C:\Apps\Registered.exe"),
            new("04", "Icon Packaged", [], ApplicationLaunchKind.Packaged,
                "Package_family!App", ExecutablePath: @"C:\Protected\App.exe"),
            new("05", "Icon Shell Item", [], ApplicationLaunchKind.ShellItem,
                @"shell:AppsFolder\Shell_family!App"),
            new("06", "Icon Settings", [], ApplicationLaunchKind.SettingsUri,
                "ms-settings:display"),
            new("07", "Icon Control Panel", [], ApplicationLaunchKind.ControlPanel,
                "Microsoft.System", ExecutablePath: @"C:\Windows\System32\control.exe"),
            new("08", "Icon Invalid", [], ApplicationLaunchKind.ShellItem,
                new string('x', InputCandidatePresentation.MaximumIconSourceLength + 1)));
        using var coordinator = new InputCandidateCoordinator(
            shell,
            files,
            new FakeFileCandidateLauncher(),
            commands,
            new InputCandidateOptions { FileCandidateCount = 10, TotalCandidateCount = 11 },
            apps,
            new TestApplicationLauncher());
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            shell.RaiseInputChanged("Icon", revision: 1);
            var candidates = await WaitApplicationSnapshotAsync(
                shell,
                1,
                predicate: items => items.Count(item => item.PrimaryText.StartsWith("Icon ",
                    StringComparison.Ordinal)) == 8);
            var sources = candidates
                .Where(candidate => candidate.PrimaryText.StartsWith("Icon ", StringComparison.Ordinal))
                .ToDictionary(candidate => candidate.PrimaryText, candidate => candidate.IconSource);
            Assert.Equal(@"C:\Menu\Shortcut.lnk", sources["Icon Shortcut"]);
            Assert.Equal(@"C:\Apps\Executable.exe", sources["Icon Executable"]);
            Assert.Equal(@"C:\Apps\Registered.exe", sources["Icon Registered"]);
            Assert.Equal(@"shell:AppsFolder\Package_family!App", sources["Icon Packaged"]);
            Assert.Equal(@"shell:AppsFolder\Shell_family!App", sources["Icon Shell Item"]);
            Assert.True(sources["Icon Settings"] is null);
            Assert.Equal(@"C:\Windows\System32\control.exe", sources["Icon Control Panel"]);
            Assert.True(sources["Icon Invalid"] is null);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task TestApplicationCandidateIsolation()
    {
        using var commands = new CommandDispatcher();
        Assert.True(commands.Register("tool", "micro.command", _ => { }));
        var shell = new FakeNativeShell();
        var files = ApplicationTestFileIndex();
        var apps = new TestApplicationCatalog(TestApplication("todo", "Microsoft To Do"));
        var launcher = new TestApplicationLauncher();
        using var coordinator = new InputCandidateCoordinator(shell, files, new FakeFileCandidateLauncher(), commands,
            new InputCandidateOptions(), apps, launcher);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            apps.FailQueries = true;
            shell.RaiseInputChanged("micro", revision: 1);
            var appFailure = await WaitApplicationSnapshotAsync(shell, 1);
            Assert.True(appFailure.Any(candidate => candidate.PrimaryText == "micro"),
                "Application discovery failure must not erase successful file candidates.");
            apps.FailQueries = false;
            files.SetQuery(static (_, _, _, _) => throw new IOException("File index unavailable."));
            files.RaiseIndexChanged();
            shell.RaiseInputChanged("micro", revision: 2);
            var fileFailure = await WaitApplicationSnapshotAsync(shell, 2);
            var appToken = fileFailure.Single(candidate => candidate.PrimaryText == "Microsoft To Do").Token;

            var appQueries = apps.QueryCount;
            var fileQueries = files.Queries.Count;
            shell.RaiseInputChanged("/tool micro", InputMode.Command, revision: 3);
            var commandsOnly = await WaitApplicationSnapshotAsync(shell, 3);
            Assert.True(commandsOnly.Count != 0 && commandsOnly.All(candidate => candidate.Kind == CandidateKind.Command));
            shell.RaiseCandidateActivated(appToken);
            Assert.Empty(launcher.Opened, "Switching to Cmd must invalidate the preceding application activation token.");
            shell.RaiseInputChanged("micro", InputMode.Ask, revision: 4);
            Assert.Empty(await WaitApplicationSnapshotAsync(shell, 4));
            Assert.Equal(appQueries, apps.QueryCount, "Cmd and Ask must not query application discovery.");
            Assert.Equal(fileQueries, files.Queries.Count, "Cmd and Ask must not query the filesystem index.");

            var delayed = new TaskCompletionSource<IReadOnlyList<FileIndexMatch>>(TaskCreationOptions.RunContinuationsAsynchronously);
            files.SetQuery((_, _, _, _) => new ValueTask<IReadOnlyList<FileIndexMatch>>(delayed.Task));
            files.RaiseIndexChanged();
            shell.RaiseInputChanged("micro", revision: 5);
            await WaitApplicationConditionAsync(() => files.Queries.Count != 0 && files.Queries[^1].Revision == 5,
                "The delayed filesystem source was not queried.");
            var pendingApps = await WaitApplicationSnapshotAsync(shell, 5);
            Assert.True(pendingApps.Any(candidate => candidate.PrimaryText == "Microsoft To Do"),
                "Application candidates must become available while the file source is pending.");
            var beforeModeChange = shell.CandidateSnapshots.Count;
            shell.RaiseInputChanged("new question", InputMode.Ask, revision: 6);
            delayed.SetResult([]);
            Assert.Empty(await WaitApplicationSnapshotAsync(shell, 6));
            Assert.False(shell.CandidateSnapshots.Skip(beforeModeChange).Any(snapshot => snapshot.Revision == 5),
                "Completing an old file query must not republish applications after the input mode changes.");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task TestApplicationCandidatePriorityOverride()
    {
        using var commands = new CommandDispatcher();
        var shell = new FakeNativeShell();
        var apps = new TestApplicationCatalog(TestApplication("todo", "Microsoft To Do"));
        var files = new FakeFileIndexClient();
        files.SetQuery(static (_, maximumResults, _, _) => ValueTask.FromResult<IReadOnlyList<FileIndexMatch>>(
            Enumerable.Range(0, 6).Select(index => new FileIndexMatch((ulong)index + 1,
                    FileSystemEntryKind.File, $"micro-{index}.txt", $@"C:\Docs\micro-{index}.txt"))
                .Append(new FileIndexMatch(7, FileSystemEntryKind.File, "micro-frequent.txt", @"C:\Docs\micro-frequent.txt"))
                .Take(maximumResults).ToArray()));
        var policy = new DefaultCandidateRankingPolicy(priorityProvider: new TestCandidatePriorityProvider(
            candidate => candidate.Identity.EndsWith(@":C:\Docs\micro-frequent.txt", StringComparison.OrdinalIgnoreCase) ? 2000 : 0));
        using var coordinator = new InputCandidateCoordinator(shell, files,
            new FakeFileCandidateLauncher(), commands, new InputCandidateOptions(), apps, new TestApplicationLauncher(), policy);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            shell.RaiseInputChanged("micro", revision: 1);
            var candidates = await WaitApplicationSnapshotAsync(shell, 1,
                predicate: candidates => candidates.Any(candidate => candidate.PrimaryText == "micro-frequent.txt"));
            Assert.Equal("micro-frequent.txt", candidates[0].PrimaryText,
                "A boosted file outside the first five retrieved entries must outrank the application after pool merging.");
            Assert.Equal("Microsoft To Do", candidates[1].PrimaryText);
            Assert.Equal(64, files.Queries.Single().MaximumResults);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task TestApplicationActivationRevision()
    {
        using var commands = new CommandDispatcher();
        var shell = new FakeNativeShell();
        var apps = new TestApplicationCatalog(TestApplication("todo", "Microsoft To Do"));
        var completion = new TaskCompletionSource<ApplicationLaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new TestApplicationLauncher { Handler = (_, _) => completion.Task };
        using var coordinator = new InputCandidateCoordinator(shell, ApplicationTestFileIndex(),
            new FakeFileCandidateLauncher(), commands, new InputCandidateOptions(), apps, launcher);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            shell.RaiseInputChanged("micro", revision: 1);
            var candidates = await WaitApplicationSnapshotAsync(shell, 1,
                predicate: candidates => candidates.Any(candidate => candidate.PrimaryText == "micro"));
            shell.RaiseCandidateActivated(candidates[0].Token);
            await WaitApplicationConditionAsync(() => launcher.Opened.Count == 1, "The asynchronous launch did not start.");
            shell.RaiseCandidateActivated(candidates[0].Token);
            Assert.Equal(1, launcher.Opened.Count, "Repeated Enter must not start a second concurrent app activation.");

            shell.RaiseInputChanged("new question", InputMode.Ask, revision: 2);
            Assert.Empty(await WaitApplicationSnapshotAsync(shell, 2));
            completion.SetResult(new ApplicationLaunchResult(true));
            shell.RaiseInputChanged("micro", revision: 3);
            var current = await WaitApplicationSnapshotAsync(shell, 3,
                predicate: items => items.Any(candidate => candidate.PrimaryText == "micro"));
            launcher.Handler = null;
            launcher.Result = new ApplicationLaunchResult(false, Cancelled: true);
            await WaitApplicationConditionAsync(() =>
            {
                if (launcher.Opened.Count == 2) return true;
                shell.RaiseCandidateActivated(current[0].Token);
                return launcher.Opened.Count == 2;
            }, "The single-activation gate did not release after the earlier launch completed.");
            await WaitApplicationConditionAsync(() => shell.EnqueuedMessages.Contains("已取消打开应用程序。"),
                "A cancelled launch did not report its result.");
            Assert.Equal(0, shell.HideCommandInputCalls,
                "A stale successful launch and a current cancelled launch must both preserve the current input.");
        }
        finally
        {
            completion.TrySetResult(new ApplicationLaunchResult(false));
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task TestStandaloneExecutablePriority()
    {
        using var commands = new CommandDispatcher();
        var shell = new FakeNativeShell();
        var files = new FakeFileIndexClient();
        files.SetQuery(static (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<FileIndexMatch>>(
        [
            new(1, FileSystemEntryKind.File, "micro", @"C:\Docs\micro"),
            new(2, FileSystemEntryKind.File, "microlink.lnk", @"C:\Docs\microlink.lnk"),
            new(3, FileSystemEntryKind.File, "microtool.exe", @"C:\Portable\microtool.exe"),
        ]));
        var launcher = new FakeFileCandidateLauncher();
        using var coordinator = new InputCandidateCoordinator(shell, files, launcher, commands, new InputCandidateOptions());
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            shell.RaiseInputChanged("micro", revision: 1);
            var candidates = await WaitApplicationSnapshotAsync(shell, 1);
            Assert.SequenceEqual(["microtool.exe", "micro", "microlink.lnk"],
                candidates.Take(3).Select(candidate => candidate.PrimaryText));
            Assert.Equal(CandidateIconKind.Executable, candidates[0].IconKind);
            Assert.Equal(@"C:\Portable\microtool.exe", candidates[0].IconSource);
            Assert.True(candidates[1].IconSource is null);
            Assert.True(candidates[2].IconSource is null);
            shell.RaiseCandidateActivated(candidates[0].Token);
            Assert.SequenceEqual([@"C:\Portable\microtool.exe"], launcher.Opened,
                "A standalone executable must retain its filesystem activation target.");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static ApplicationEntry TestApplication(string id, string name, string[]? aliases = null) =>
        new(id, name, aliases ?? [], ApplicationLaunchKind.Executable, $@"C:\Apps\{id}.exe",
            ExecutablePath: $@"C:\Apps\{id}.exe", Source: id);

    private static FakeFileIndexClient ApplicationTestFileIndex()
    {
        var files = new FakeFileIndexClient();
        files.SetQuery(static (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<FileIndexMatch>>(
            [new(1, FileSystemEntryKind.File, "micro", @"C:\Docs\micro")]));
        return files;
    }

    private static async Task<IReadOnlyList<InputCandidate>> WaitApplicationSnapshotAsync(
        FakeNativeShell shell, ulong revision, int afterCount = 0,
        Func<IReadOnlyList<InputCandidate>, bool>? predicate = null)
    {
        IReadOnlyList<InputCandidate>? result = null;
        await WaitApplicationConditionAsync(() =>
        {
            for (var index = shell.CandidateSnapshots.Count - 1; index >= afterCount; index--)
            {
                var snapshot = shell.CandidateSnapshots[index];
                if (snapshot.Revision == revision && (predicate is null || predicate(snapshot.Candidates)))
                {
                    result = snapshot.Candidates;
                    return true;
                }
            }
            return false;
        }, $"Candidate revision {revision} was not published.");
        return Assert.NotNull(result);
    }

    private static async Task WaitApplicationConditionAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), message);
    }

    private sealed class TestCandidatePriorityProvider(Func<CandidateRankingContext, double> score) : ICandidatePriorityProvider
    {
        public double GetBoost(CandidateRankingContext candidate) => score(candidate);
    }

    private sealed class TestApplicationCatalog(params ApplicationEntry[] initialEntries) : IApplicationCatalog
    {
        private ApplicationEntry[] entries = initialEntries;
        private int queryCount;
        public event Action? Changed;
        public int QueryCount => Volatile.Read(ref queryCount);
        public bool FailQueries { get; set; }
        public void SetEntries(ApplicationEntry[] value) => Volatile.Write(ref entries, value);
        public void RaiseChanged() => Changed?.Invoke();
        public void RequestRefresh() => RaiseChanged();
        public IReadOnlyList<ApplicationMatch> Query(string query, int maximumResults)
        {
            Interlocked.Increment(ref queryCount);
            if (FailQueries) throw new InvalidOperationException("Application catalog unavailable.");
            return Volatile.Read(ref entries).Select(entry => (Entry: entry, Score: ApplicationNameMatcher.Score(entry, query)))
                .Where(item => item.Score.HasValue)
                .OrderByDescending(item => item.Score).ThenBy(item => item.Entry.Id, StringComparer.Ordinal)
                .Take(maximumResults).Select(item => new ApplicationMatch(item.Entry, item.Score!.Value)).ToArray();
        }
        public bool TryGet(string id, out ApplicationEntry? entry)
        {
            entry = Volatile.Read(ref entries).FirstOrDefault(item => item.Id == id);
            return entry is not null;
        }
    }

    private sealed class TestApplicationLauncher : IApplicationLauncher
    {
        public ConcurrentQueue<ApplicationEntry> Opened { get; } = new();
        public ConcurrentQueue<ApplicationEntry> Revealed { get; } = new();
        public ApplicationLaunchResult Result { get; set; } = new(true);
        public Func<ApplicationEntry, CancellationToken, Task<ApplicationLaunchResult>>? Handler { get; set; }
        public Task<ApplicationLaunchResult> OpenAsync(ApplicationEntry entry, CancellationToken cancellationToken)
        {
            Opened.Enqueue(entry);
            return Handler?.Invoke(entry, cancellationToken) ?? Task.FromResult(Result);
        }
        public Task<ApplicationLaunchResult> RevealAsync(ApplicationEntry entry, CancellationToken cancellationToken)
        {
            Revealed.Enqueue(entry);
            return Handler?.Invoke(entry, cancellationToken) ?? Task.FromResult(Result);
        }
    }
}
