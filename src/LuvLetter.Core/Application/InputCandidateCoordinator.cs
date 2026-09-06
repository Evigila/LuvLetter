using System.Threading.Channels;
using ArkheideSystem;
using Microsoft.Extensions.Hosting;
using LuvLetter.Core.Commands;
using LuvLetter.Core.NativeShell;

namespace LuvLetter.Core.Application;

/// <summary>
/// Turns editor revisions into a bounded candidate snapshot. The pending channel holds
/// only the newest edit, and revisions are checked both before querying and publishing.
/// </summary>
public sealed class InputCandidateCoordinator : IHostedService, IDisposable
{
    private readonly INativeShell nativeShell;
    private readonly IFileIndexClient fileIndexClient;
    private readonly IFileCandidateLauncher fileLauncher;
    private readonly CommandDispatcher commandDispatcher;
    private readonly InputCandidateOptions options;
    private readonly IApplicationCatalog? applicationCatalog;
    private readonly IApplicationLauncher? applicationLauncher;
    private readonly ICandidateRankingPolicy rankingPolicy;
    private readonly IClipboard? clipboard;
    private readonly Channel<InputChanged> pendingChanges;
    private readonly object stateLock = new();
    private readonly object indexActivityLock = new();
    private IReadOnlyDictionary<ulong, CandidateTarget> activeTargets =
        new Dictionary<ulong, CandidateTarget>();
    private IReadOnlyDictionary<string, ulong> activeIdentityTokens =
        new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? lifetimeCancellation;
    private CancellationTokenSource? activeQueryCancellation;
    private InputChanged? lastInputChange;
    private Task? consumeTask;
    private IMessageActivity? indexMessageActivity;
    private FileIndexRuntimeActivity displayedIndexActivity = FileIndexRuntimeActivity.Unavailable;
    private string? displayedIndexMessage;
    private bool indexWorkObserved;
    private ulong latestRevision;
    private ulong activeCandidateRevision;
    private long nextToken;
    private int started;
    private int disposed;
    private int applicationActivationPending;
    private long applicationIndexRevision;
    private long fileIndexRevision;
    private long cachedApplicationIndexRevision = -1;
    private long cachedFileIndexRevision = -1;
    private string? cachedApplicationQuery;
    private IReadOnlyList<ApplicationMatch> cachedApplicationMatches = [];
    private string? cachedFileQuery;
    private IReadOnlyList<FileIndexMatch> cachedFileMatches = [];

    public InputCandidateCoordinator(
        INativeShell nativeShell,
        IFileIndexClient fileIndexClient,
        IFileCandidateLauncher fileLauncher,
        CommandDispatcher commandDispatcher,
        InputCandidateOptions options,
        IApplicationCatalog? applicationCatalog = null,
        IApplicationLauncher? applicationLauncher = null,
        ICandidateRankingPolicy? rankingPolicy = null,
        IClipboard? clipboard = null)
    {
        ArgumentNullException.ThrowIfNull(nativeShell);
        ArgumentNullException.ThrowIfNull(fileIndexClient);
        ArgumentNullException.ThrowIfNull(fileLauncher);
        ArgumentNullException.ThrowIfNull(commandDispatcher);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        this.nativeShell = nativeShell;
        this.fileIndexClient = fileIndexClient;
        this.fileLauncher = fileLauncher;
        this.commandDispatcher = commandDispatcher;
        this.options = options;
        this.applicationCatalog = applicationCatalog;
        this.applicationLauncher = applicationLauncher;
        this.rankingPolicy = rankingPolicy ?? new DefaultCandidateRankingPolicy();
        this.clipboard = clipboard;
        pendingChanges = Channel.CreateBounded<InputChanged>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false,
            });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("The input candidate coordinator has already started.");
        }

        lifetimeCancellation = new CancellationTokenSource();
        nativeShell.InputChanged += HandleInputChanged;
        nativeShell.CandidateActivated += HandleCandidateActivated;
        fileIndexClient.IndexChanged += HandleFileIndexChanged;
        fileIndexClient.StateChanged += HandleIndexStateChanged;
        if (applicationCatalog is not null) applicationCatalog.Changed += HandleApplicationIndexChanged;
        consumeTask = ConsumeAsync(lifetimeCancellation.Token);
        HandleIndexStateChanged(fileIndexClient.CurrentState);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 0) == 0)
        {
            return;
        }

        fileIndexClient.StateChanged -= HandleIndexStateChanged;
        if (applicationCatalog is not null) applicationCatalog.Changed -= HandleApplicationIndexChanged;
        fileIndexClient.IndexChanged -= HandleFileIndexChanged;
        nativeShell.CandidateActivated -= HandleCandidateActivated;
        nativeShell.InputChanged -= HandleInputChanged;

        CancellationTokenSource? lifetime;
        CancellationTokenSource? query;
        Task? consumer;
        lock (stateLock)
        {
            lifetime = lifetimeCancellation;
            lifetimeCancellation = null;
            query = activeQueryCancellation;
            activeQueryCancellation = null;
            consumer = consumeTask;
            consumeTask = null;
        }

        query?.Cancel();
        lifetime?.Cancel();
        if (consumer is not null)
        {
            try
            {
                await consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true)
            {
            }
        }

        query?.Dispose();
        lifetime?.Dispose();
        Volatile.Write(
            ref activeTargets,
            new Dictionary<ulong, CandidateTarget>());
        activeIdentityTokens = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        activeCandidateRevision = 0;
        cachedApplicationQuery = null;
        cachedApplicationMatches = [];
        cachedFileQuery = null;
        cachedFileMatches = [];
        EndIndexActivity(sendReadyMessage: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        nativeShell.CandidateActivated -= HandleCandidateActivated;
        nativeShell.InputChanged -= HandleInputChanged;
        fileIndexClient.IndexChanged -= HandleFileIndexChanged;
        fileIndexClient.StateChanged -= HandleIndexStateChanged;
        if (applicationCatalog is not null) applicationCatalog.Changed -= HandleApplicationIndexChanged;
        pendingChanges.Writer.TryComplete();
        lock (stateLock)
        {
            activeQueryCancellation?.Cancel();
            lifetimeCancellation?.Cancel();
        }
        EndIndexActivity(sendReadyMessage: false);
    }

    private void HandleInputChanged(InputChanged change)
    {
        if (Volatile.Read(ref started) == 0 || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        lock (stateLock)
        {
            if (change.Revision < latestRevision)
            {
                return;
            }

            latestRevision = change.Revision;
            lastInputChange = change;
            activeQueryCancellation?.Cancel();
        }

        pendingChanges.Writer.TryWrite(change);
    }

    private void HandleFileIndexChanged()
    {
        Interlocked.Increment(ref fileIndexRevision);
        HandleIndexChanged();
    }

    private void HandleApplicationIndexChanged()
    {
        Interlocked.Increment(ref applicationIndexRevision);
        HandleIndexChanged();
    }

    private void HandleIndexChanged()
    {
        InputChanged? change;
        lock (stateLock)
        {
            change = lastInputChange;
            if (Volatile.Read(ref started) == 0
                || change is null
                || change.Revision != latestRevision
                || string.IsNullOrWhiteSpace(change.Text))
            {
                return;
            }

            activeQueryCancellation?.Cancel();
        }

        pendingChanges.Writer.TryWrite(change);
    }

    private void HandleIndexStateChanged(FileIndexRuntimeState state)
    {
        if (Volatile.Read(ref started) == 0 || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        lock (indexActivityLock)
        {
            if (Volatile.Read(ref started) == 0 || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            switch (state.Activity)
            {
                case FileIndexRuntimeActivity.InitialBuild:
                    indexWorkObserved = true;
                    ShowOrUpdateIndexActivity(
                        FileIndexRuntimeActivity.InitialBuild,
                        FormatIndexActivity("正在生成索引表", state));
                    break;
                case FileIndexRuntimeActivity.Updating:
                    indexWorkObserved = true;
                    ShowOrUpdateIndexActivity(
                        FileIndexRuntimeActivity.Updating,
                        FormatIndexActivity("正在更新索引", state));
                    break;
                case FileIndexRuntimeActivity.Ready:
                    EndIndexActivityLocked(sendReadyMessage: indexWorkObserved);
                    indexWorkObserved = false;
                    break;
                case FileIndexRuntimeActivity.Failed:
                    EndIndexActivityLocked(sendReadyMessage: false);
                    if (indexWorkObserved)
                    {
                        indexWorkObserved = false;
                        try
                        {
                            nativeShell.EnqueueMessage("索引更新失败，将稍后重试");
                        }
                        catch
                        {
                            // Index status presentation must not terminate candidate coordination.
                        }
                    }
                    break;
                case FileIndexRuntimeActivity.Unavailable:
                    EndIndexActivityLocked(sendReadyMessage: false);
                    break;
            }
        }
    }

    private void ShowOrUpdateIndexActivity(
        FileIndexRuntimeActivity activity,
        string message)
    {
        if (displayedIndexActivity == activity
            && displayedIndexMessage == message
            && indexMessageActivity is not null)
        {
            return;
        }

        try
        {
            if (indexMessageActivity is null)
            {
                indexMessageActivity = nativeShell.BeginMessageActivity(message);
            }
            else
            {
                indexMessageActivity.Update(message);
            }
            displayedIndexActivity = activity;
            displayedIndexMessage = message;
        }
        catch
        {
            indexMessageActivity = null;
            displayedIndexActivity = FileIndexRuntimeActivity.Unavailable;
            displayedIndexMessage = null;
        }
    }

    private static string FormatIndexActivity(
        string title,
        FileIndexRuntimeState state)
    {
        var stage = state.Stage switch
        {
            FileIndexRuntimeStage.Recovering => "恢复中",
            FileIndexRuntimeStage.Scanning => "扫描中",
            FileIndexRuntimeStage.Packing => "整理中",
            FileIndexRuntimeStage.Compacting => "压缩中",
            FileIndexRuntimeStage.Persisting => "保存中",
            _ => string.Empty,
        };
        var message = string.IsNullOrEmpty(stage) ? title : $"{title} · {stage}";
        if (state.ProgressPercent is { } percent)
        {
            var estimate = state.ProgressIsEstimated ? "约" : string.Empty;
            message = $"{message} · {estimate}{percent}%";
        }
        if (state.Stage == FileIndexRuntimeStage.Scanning && state.DiscoveredEntries > 0)
        {
            message = $"{message} · {state.DiscoveredEntries:N0} 项";
        }
        return message;
    }

    private void EndIndexActivity(bool sendReadyMessage)
    {
        lock (indexActivityLock)
        {
            EndIndexActivityLocked(sendReadyMessage);
            if (!sendReadyMessage)
            {
                indexWorkObserved = false;
            }
        }
    }

    private void EndIndexActivityLocked(bool sendReadyMessage)
    {
        var activity = indexMessageActivity;
        indexMessageActivity = null;
        displayedIndexActivity = FileIndexRuntimeActivity.Unavailable;
        displayedIndexMessage = null;

        if (activity is not null)
        {
            try
            {
                if (sendReadyMessage)
                {
                    activity.Complete("索引已就绪");
                }
                else
                {
                    activity.Dispose();
                }
                return;
            }
            catch
            {
                try
                {
                    activity.Dispose();
                }
                catch
                {
                }
            }
        }

        if (sendReadyMessage)
        {
            try
            {
                nativeShell.EnqueueMessage("索引已就绪");
            }
            catch
            {
                // Index status presentation must not terminate candidate coordination.
            }
        }
    }

    private void HandleCandidateActivated(CandidateActivated activation)
    {
        if (Volatile.Read(ref started) == 0
            || activation.Token == 0
            || !Volatile.Read(ref activeTargets).TryGetValue(activation.Token, out var target)
            || !IsLatest(target.Revision)
            || !SupportsAction(target.Actions, activation.Action))
        {
            return;
        }

        if (target.ApplicationId is not null)
        {
            if (activation.Action == CandidateAction.CopyPath)
            {
                CopyCandidatePath(target.CopyText);
                return;
            }
            _ = ActivateApplicationAsync(target, activation.Action);
            return;
        }

        switch (target.Kind)
        {
            case CandidateKind.File:
                if (activation.Action == CandidateAction.CopyPath)
                {
                    CopyCandidatePath(target.CopyText);
                }
                else if (target.EntryKind is { } entryKind
                    && target.ExecutionText is not null)
                {
                    ActivateFileSystemEntry(target.ExecutionText, entryKind, activation.Action);
                }
                break;
            case CandidateKind.Command:
                if (activation.Action == CandidateAction.Complete
                    && target.CompletionText is not null)
                {
                    ReplaceCommandInput(target.CompletionText);
                }
                else if (activation.Action == CandidateAction.Open
                    && target.ExecutionText is not null)
                {
                    ActivateCommand(target.ExecutionText);
                }
                break;
            case CandidateKind.GlobalSearch:
                ReportStatus(options.GlobalSearchUnavailableMessage);
                break;
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await pendingChanges.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                InputChanged change = default!;
                while (pendingChanges.Reader.TryRead(out var next))
                {
                    change = next;
                }

                if (change is null || !IsLatest(change.Revision))
                {
                    continue;
                }

                using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                lock (stateLock)
                {
                    activeQueryCancellation?.Dispose();
                    activeQueryCancellation = queryCancellation;
                }

                try
                {
                    await ProcessChangeAsync(change, queryCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (queryCancellation.IsCancellationRequested)
                {
                }
                catch
                {
                    // Candidate production is optional and must not terminate the input surface.
                    try
                    {
                        Publish([], change.Revision);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    lock (stateLock)
                    {
                        if (ReferenceEquals(activeQueryCancellation, queryCancellation))
                        {
                            activeQueryCancellation = null;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessChangeAsync(InputChanged change, CancellationToken cancellationToken)
    {
        if (!IsLatest(change.Revision))
        {
            return;
        }

        var query = change.Text.Trim();
        if (change.Mode == InputMode.Ask)
        {
            Publish([], change.Revision);
            return;
        }

        if (change.Mode == InputMode.Command)
        {
            var commandInput = CommandInputSyntax.RemoveModePrefixForCompletion(change.Text);
            Publish(BuildCommandCandidates(commandInput, options.TotalCandidateCount), change.Revision);
            return;
        }

        if (change.Mode != InputMode.General || query.Length == 0)
        {
            Publish([], change.Revision);
            return;
        }

        var directLimit = Math.Min(
            options.FileCandidateCount,
            Math.Max(0, options.TotalCandidateCount - 1));
        var retrievalLimit = Math.Max(directLimit, options.RetrievalCandidateCount);
        var applicationRevision = Volatile.Read(ref applicationIndexRevision);
        IReadOnlyList<ApplicationMatch> applications = [];
        var applicationQuerySucceeded = false;
        if (directLimit > 0 && applicationCatalog is not null)
        {
            if (cachedApplicationIndexRevision == applicationRevision
                && string.Equals(cachedApplicationQuery, query, StringComparison.Ordinal))
            {
                applications = cachedApplicationMatches;
                applicationQuerySucceeded = true;
            }
            else
            {
                try
                {
                    applications = applicationCatalog.Query(query, retrievalLimit);
                    applicationQuerySucceeded = true;
                }
                catch
                {
                    // A catalog failure must not prevent filesystem candidates.
                }
            }
        }
        if (applicationQuerySucceeded
            && applicationRevision == Volatile.Read(ref applicationIndexRevision))
        {
            cachedApplicationQuery = query;
            cachedApplicationMatches = applications;
            cachedApplicationIndexRevision = applicationRevision;
        }
        if (applications.Count > 0 && activeCandidateRevision != change.Revision)
        {
            // Applications can be shown immediately while the companion query runs.
            var fileRevision = Volatile.Read(ref fileIndexRevision);
            Publish(MergeSearchCandidates(query, directLimit, applications,
                cachedFileIndexRevision == fileRevision
                    && string.Equals(cachedFileQuery, query, StringComparison.Ordinal)
                        ? cachedFileMatches
                        : []), change.Revision);
        }
        var fileRevisionAtQuery = Volatile.Read(ref fileIndexRevision);
        IReadOnlyList<FileIndexMatch> files = [];
        var fileQuerySucceeded = false;
        if (directLimit > 0)
        {
            if (cachedFileIndexRevision == fileRevisionAtQuery
                && string.Equals(cachedFileQuery, query, StringComparison.Ordinal))
            {
                files = cachedFileMatches;
                fileQuerySucceeded = true;
            }
            else
            {
                try
                {
                    files = await fileIndexClient.QueryAsync(
                        query,
                        retrievalLimit,
                        change.Revision,
                        cancellationToken).ConfigureAwait(false);
                    fileQuerySucceeded = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // The companion is optional; commands and Global Search remain available.
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLatest(change.Revision))
        {
            return;
        }

        if (fileQuerySucceeded
            && fileRevisionAtQuery == Volatile.Read(ref fileIndexRevision))
        {
            cachedFileQuery = query;
            cachedFileMatches = files;
            cachedFileIndexRevision = fileRevisionAtQuery;
        }
        Publish(MergeSearchCandidates(query, directLimit, applications, files), change.Revision);
    }

    private IReadOnlyList<CandidateSpec> MergeSearchCandidates(
        string query, int directLimit, IReadOnlyList<ApplicationMatch> applications,
        IReadOnlyList<FileIndexMatch> files)
    {
        var ranked = new List<(CandidateSpec Spec, double Score)>(applications.Count + files.Count);
        var applicationFiles = new HashSet<string>(applications.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var match in applications.DistinctBy(match => match.Entry.Id, StringComparer.OrdinalIgnoreCase))
        {
            var entry = match.Entry;
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.DisplayName)) continue;
            var identity = $"app:{entry.Id}";
            var candidatePath = CandidatePathForApplication(entry);
            var actions = CandidateActions.Open;
            if (candidatePath is not null)
            {
                actions |= CandidateActions.Reveal | CandidateActions.CopyPath;
            }
            var spec = new CandidateSpec(CandidateKind.File, CandidateIconKind.Executable,
                entry.DisplayName, ApplicationDescription(candidatePath), entry.LaunchTarget, null, identity,
                IconSourceForApplication(entry), entry.Id, Actions: actions, CopyText: candidatePath);
            ranked.Add((spec, rankingPolicy.Score(new CandidateRankingContext(
                identity, query, SearchCandidateSource.Application, match.MatchScore))));

            // A shortcut file is the exact same launch entry. Bare executable
            // equivalents are collapsed only when arguments/search paths cannot differ.
            if (entry.LaunchKind == ApplicationLaunchKind.Shortcut)
                applicationFiles.Add(entry.LaunchTarget);
            if ((entry.LaunchKind is ApplicationLaunchKind.Executable or ApplicationLaunchKind.RegisteredExecutable)
                && string.IsNullOrEmpty(entry.Arguments) && string.IsNullOrEmpty(entry.SearchPath)
                && !string.IsNullOrEmpty(entry.ExecutablePath)
                && (string.IsNullOrEmpty(entry.WorkingDirectory)
                    || string.Equals(Path.TrimEndingDirectorySeparator(entry.WorkingDirectory),
                        Path.GetDirectoryName(entry.ExecutablePath), StringComparison.OrdinalIgnoreCase)))
                applicationFiles.Add(entry.ExecutablePath);
        }

        var candidates = new List<CandidateSpec>(options.TotalCandidateCount);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.DisplayName)
                || string.IsNullOrWhiteSpace(file.FullPath)
                || !Enum.IsDefined(file.EntryKind))
            {
                continue;
            }

            if (applicationFiles.Contains(file.FullPath)) continue;
            var identity = $"fs:{file.StableId:X16}:{file.FullPath}";
            var isExecutable = file.EntryKind == FileSystemEntryKind.File
                && Path.GetExtension(file.FullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase);
            var spec = new CandidateSpec(
                CandidateKind.File,
                CandidateIconClassifier.Classify(file.EntryKind, file.FullPath),
                file.DisplayName,
                $"{CandidateTypeLabel(file.EntryKind, isExecutable)} · {ParentPath(file.FullPath)}",
                file.FullPath,
                file.EntryKind,
                identity,
                isExecutable ? InputCandidatePresentation.NormalizeIconSource(file.FullPath) : null,
                CopyText: file.FullPath);
            ranked.Add((spec, rankingPolicy.Score(new CandidateRankingContext(
                identity, query, isExecutable ? SearchCandidateSource.Application
                    : file.EntryKind == FileSystemEntryKind.Directory ? SearchCandidateSource.Directory : SearchCandidateSource.File,
                DefaultCandidateRankingPolicy.FileMatchScore(file, query)))));
        }

        candidates.AddRange(ranked.OrderByDescending(item => double.IsFinite(item.Score)
                ? item.Score : double.MinValue)
            .Select(item => item.Spec).DistinctBy(spec => spec.Identity, StringComparer.OrdinalIgnoreCase)
            .Take(directLimit));

        if (candidates.Count < options.TotalCandidateCount)
        {
            candidates.Add(new CandidateSpec(
                CandidateKind.GlobalSearch,
                CandidateIconKind.Search,
                options.GlobalSearchLabel,
                $"{options.GlobalSearchDescription}: {query}",
                query,
                null,
                $"global:{query}"));
        }

        return candidates;
    }

    private static string ApplicationDescription(string? candidatePath) =>
        candidatePath is null ? "应用" : $"应用 · {candidatePath}";

    private static string CandidateTypeLabel(FileSystemEntryKind entryKind, bool isExecutable) =>
        isExecutable ? "应用"
            : entryKind == FileSystemEntryKind.Directory ? "文件夹" : "文件";

    private static string? CandidatePathForApplication(ApplicationEntry entry)
    {
        var candidate = entry.LaunchKind switch
        {
            ApplicationLaunchKind.Shortcut => entry.ExecutablePath ?? entry.LaunchTarget,
            ApplicationLaunchKind.Packaged => entry.ExecutablePath ?? entry.InstallDirectory,
            _ => entry.ExecutablePath ?? entry.InstallDirectory ?? entry.LaunchTarget,
        };
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var normalized = candidate.Trim();
        return normalized.IndexOfAny(['\0', '\r', '\n']) < 0 ? normalized : null;
    }

    private static string? IconSourceForApplication(ApplicationEntry entry)
    {
        var source = entry.LaunchKind switch
        {
            ApplicationLaunchKind.Shortcut => entry.LaunchTarget,
            ApplicationLaunchKind.Executable => entry.ExecutablePath ?? entry.LaunchTarget,
            ApplicationLaunchKind.RegisteredExecutable => entry.ExecutablePath,
            ApplicationLaunchKind.Packaged => entry.LaunchTarget.StartsWith(
                    "shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                ? entry.LaunchTarget
                : "shell:AppsFolder\\" + entry.LaunchTarget,
            ApplicationLaunchKind.ShellItem => entry.LaunchTarget,
            ApplicationLaunchKind.SettingsUri or ApplicationLaunchKind.ControlPanel => entry.ExecutablePath,
            _ => null,
        };
        return InputCandidatePresentation.NormalizeIconSource(source);
    }

    private IReadOnlyList<CandidateSpec> BuildCommandCandidates(string input, int limit)
    {
        return commandDispatcher.Suggest(input, limit)
            .Select(suggestion => new CandidateSpec(
                CandidateKind.Command,
                CandidateIconKind.Command,
                suggestion.Label,
                suggestion.Description,
                suggestion.ExecutionText,
                null,
                $"command:{suggestion.Kind}:{suggestion.ExecutionText}",
                CompletionText: suggestion.CompletionText,
                Actions: CandidateActions.Complete
                    | (suggestion.CanExecute ? CandidateActions.Open : CandidateActions.None)))
            .ToArray();
    }

    private void Publish(IReadOnlyList<CandidateSpec> specs, ulong revision)
    {
        if (!IsLatest(revision))
        {
            return;
        }

        var candidates = new InputCandidate[specs.Count];
        var targets = new Dictionary<ulong, CandidateTarget>(specs.Count);
        var identityTokens = new Dictionary<string, ulong>(
            specs.Count,
            StringComparer.OrdinalIgnoreCase);
        var reusableTokens = revision == activeCandidateRevision
            ? activeIdentityTokens
            : new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < specs.Count; index++)
        {
            var spec = specs[index];
            var token = reusableTokens.TryGetValue(spec.Identity, out var reusableToken)
                ? reusableToken
                : NextToken();
            var actions = spec.Actions ?? DefaultActions(spec.Kind);
            candidates[index] = new InputCandidate(
                token,
                spec.Kind,
                spec.IconKind,
                InputCandidatePresentation.NormalizePrimaryText(spec.PrimaryText),
                InputCandidatePresentation.NormalizeSecondaryText(spec.SecondaryText),
                spec.IconSource,
                actions);
            targets.Add(token, new CandidateTarget(
                spec.Kind,
                spec.ExecutionText,
                spec.CompletionText,
                spec.EntryKind,
                spec.ApplicationId,
                spec.CopyText,
                revision,
                actions));
            identityTokens.Add(spec.Identity, token);
        }

        if (!IsLatest(revision))
        {
            return;
        }

        var previousTargets = Volatile.Read(ref activeTargets);
        var transitionTargets = new Dictionary<ulong, CandidateTarget>(previousTargets);
        foreach (var target in targets)
        {
            transitionTargets[target.Key] = target.Value;
        }

        Volatile.Write(ref activeTargets, transitionTargets);
        InputCandidateSetResult result;
        try
        {
            result = nativeShell.SetInputCandidates(candidates, revision);
        }
        catch
        {
            Volatile.Write(ref activeTargets, previousTargets);
            throw;
        }

        if (result == InputCandidateSetResult.Stale)
        {
            Volatile.Write(ref activeTargets, previousTargets);
            return;
        }

        Volatile.Write(ref activeTargets, targets);
        activeIdentityTokens = identityTokens;
        activeCandidateRevision = revision;
    }

    private bool IsLatest(ulong revision)
    {
        lock (stateLock)
        {
            return revision == latestRevision && Volatile.Read(ref started) != 0;
        }
    }

    private ulong NextToken()
    {
        var token = unchecked((ulong)Interlocked.Increment(ref nextToken));
        return token == 0
            ? unchecked((ulong)Interlocked.Increment(ref nextToken))
            : token;
    }

    private void ActivateFileSystemEntry(
        string fullPath,
        FileSystemEntryKind entryKind,
        CandidateAction action)
    {
        try
        {
            var started = action == CandidateAction.Reveal
                && entryKind != FileSystemEntryKind.Directory
                ? fileLauncher.Reveal(fullPath, entryKind)
                : fileLauncher.Open(fullPath, entryKind);
            if (started)
            {
                nativeShell.DismissCommandInput();
                return;
            }

            ReportStatus($"The indexed item is no longer available: {fullPath}");
        }
        catch (Exception exception)
        {
            ReportStatus($"Cannot activate indexed item: {exception.Message}");
        }
    }

    private async Task ActivateApplicationAsync(CandidateTarget target, CandidateAction action)
    {
        if (Interlocked.CompareExchange(ref applicationActivationPending, 1, 0) != 0) return;
        try
        {
            if (applicationCatalog is null || applicationLauncher is null || target.ApplicationId is null
                || !applicationCatalog.TryGet(target.ApplicationId, out var entry) || entry is null)
            {
                ReportStatus("应用程序已不可用，请刷新应用索引。");
                return;
            }
            var cancellationToken = lifetimeCancellation?.Token ?? CancellationToken.None;
            cancellationToken.ThrowIfCancellationRequested();
            var result = action == CandidateAction.Reveal
                ? await applicationLauncher.RevealAsync(entry, cancellationToken).ConfigureAwait(false)
                : await applicationLauncher.OpenAsync(entry, cancellationToken).ConfigureAwait(false);
            if (!IsLatest(target.Revision)) return;
            if (result.Succeeded) nativeShell.DismissCommandInput();
            else ReportStatus(result.Message ?? (result.Cancelled ? "已取消打开应用程序。" : "无法打开应用程序。"));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (IsLatest(target.Revision)) ReportStatus($"无法打开应用程序：{exception.Message}");
        }
        finally { Volatile.Write(ref applicationActivationPending, 0); }
    }

    private void ActivateCommand(string commandText)
    {
        try
        {
            var result = commandDispatcher.Dispatch(commandText);
            if (result == CommandDispatchResult.Accepted)
            {
                nativeShell.HideCommandInput();
                return;
            }

            ReportStatus($"Command was not accepted: {result}");
        }
        catch (Exception exception)
        {
            ReportStatus($"Cannot activate command candidate: {exception.Message}");
        }
    }

    private void CopyCandidatePath(string? path)
    {
        try
        {
            if (path is null || clipboard?.TrySetText(path) != true)
            {
                ReportStatus("无法复制候选项路径。");
                return;
            }
        }
        catch (Exception exception)
        {
            ReportStatus($"无法复制候选项路径：{exception.Message}");
        }
    }

    private void ReportStatus(string message)
    {
        try
        {
            nativeShell.EnqueueMessage(message);
        }
        catch
        {
            // Candidate failures cannot terminate native callback delivery.
        }
    }

    private static string ParentPath(string fullPath)
    {
        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(fullPath);
            return Path.GetDirectoryName(normalized) is { Length: > 0 } parent
                ? parent
                : fullPath;
        }
        catch (ArgumentException)
        {
            return fullPath;
        }
    }

    private static int IndexOfWhitespace(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private readonly record struct CandidateSpec(
        CandidateKind Kind,
        CandidateIconKind IconKind,
        string PrimaryText,
        string SecondaryText,
        string? ExecutionText,
        FileSystemEntryKind? EntryKind,
        string Identity,
        string? IconSource = null,
        string? ApplicationId = null,
        string? CompletionText = null,
        CandidateActions? Actions = null,
        string? CopyText = null);

    private readonly record struct CandidateTarget(
        CandidateKind Kind,
        string? ExecutionText,
        string? CompletionText,
        FileSystemEntryKind? EntryKind,
        string? ApplicationId,
        string? CopyText,
        ulong Revision,
        CandidateActions Actions);

    private static CandidateActions DefaultActions(CandidateKind kind) => kind switch
    {
        CandidateKind.File => CandidateActions.Open | CandidateActions.Reveal | CandidateActions.CopyPath,
        CandidateKind.GlobalSearch => CandidateActions.Open,
        _ => CandidateActions.None,
    };

    private static bool SupportsAction(CandidateActions actions, CandidateAction action)
    {
        var required = action switch
        {
            CandidateAction.Open => CandidateActions.Open,
            CandidateAction.Reveal => CandidateActions.Reveal,
            CandidateAction.Complete => CandidateActions.Complete,
            CandidateAction.CopyPath => CandidateActions.CopyPath,
            _ => CandidateActions.None,
        };
        return required != CandidateActions.None && (actions & required) != 0;
    }

    private void ReplaceCommandInput(string commandText)
    {
        try
        {
            nativeShell.ReplaceCommandInput(commandText);
        }
        catch (Exception exception)
        {
            ReportStatus($"Cannot complete command path: {exception.Message}");
        }
    }
}
