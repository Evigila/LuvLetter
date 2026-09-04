using System.Threading.Channels;
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
    private bool indexWorkObserved;
    private ulong latestRevision;
    private ulong activeCandidateRevision;
    private long nextToken;
    private int started;
    private int disposed;

    public InputCandidateCoordinator(
        INativeShell nativeShell,
        IFileIndexClient fileIndexClient,
        IFileCandidateLauncher fileLauncher,
        CommandDispatcher commandDispatcher,
        InputCandidateOptions options)
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
        fileIndexClient.IndexChanged += HandleIndexChanged;
        fileIndexClient.StateChanged += HandleIndexStateChanged;
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
        fileIndexClient.IndexChanged -= HandleIndexChanged;
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
        fileIndexClient.IndexChanged -= HandleIndexChanged;
        fileIndexClient.StateChanged -= HandleIndexStateChanged;
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
                        "正在生成索引表");
                    break;
                case FileIndexRuntimeActivity.Updating:
                    indexWorkObserved = true;
                    ShowOrUpdateIndexActivity(
                        FileIndexRuntimeActivity.Updating,
                        "正在更新索引");
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
        if (displayedIndexActivity == activity && indexMessageActivity is not null)
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
        }
        catch
        {
            indexMessageActivity = null;
            displayedIndexActivity = FileIndexRuntimeActivity.Unavailable;
        }
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
            || !Volatile.Read(ref activeTargets).TryGetValue(activation.Token, out var target))
        {
            return;
        }

        switch (target.Kind)
        {
            case CandidateKind.File:
                if (target.EntryKind is { } entryKind)
                {
                    ActivateFileSystemEntry(target.Value, entryKind, activation.Action);
                }
                break;
            case CandidateKind.Command:
                ActivateCommand(target.Value);
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
        if (query.Length == 0 || change.Mode == InputMode.Ask)
        {
            Publish([], change.Revision);
            return;
        }

        if (change.Mode == InputMode.Command)
        {
            Publish(BuildCommandCandidates(query, options.TotalCandidateCount), change.Revision);
            return;
        }

        if (change.Mode != InputMode.General)
        {
            Publish([], change.Revision);
            return;
        }

        var directLimit = Math.Min(
            options.FileCandidateCount,
            Math.Max(0, options.TotalCandidateCount - 1));
        IReadOnlyList<FileIndexMatch> files = [];
        if (directLimit > 0)
        {
            try
            {
                files = await fileIndexClient.QueryAsync(
                    query,
                    directLimit,
                    change.Revision,
                    cancellationToken).ConfigureAwait(false);
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

        if (!IsLatest(change.Revision))
        {
            return;
        }

        var candidates = new List<CandidateSpec>(options.TotalCandidateCount);
        foreach (var file in files.Take(directLimit))
        {
            if (string.IsNullOrWhiteSpace(file.DisplayName)
                || string.IsNullOrWhiteSpace(file.FullPath)
                || !Enum.IsDefined(file.EntryKind))
            {
                continue;
            }

            candidates.Add(new CandidateSpec(
                CandidateKind.File,
                CandidateIconClassifier.Classify(file.EntryKind, file.FullPath),
                file.DisplayName,
                ParentPath(file.FullPath),
                file.FullPath,
                file.EntryKind,
                $"fs:{file.StableId:X16}:{file.FullPath}"));
            if (candidates.Count == directLimit)
            {
                break;
            }
        }

        var commandCapacity = directLimit - candidates.Count;
        if (commandCapacity > 0)
        {
            candidates.AddRange(BuildCommandCandidates(query, commandCapacity));
        }

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

        Publish(candidates, change.Revision);
    }

    private IReadOnlyList<CandidateSpec> BuildCommandCandidates(string input, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        var commandPrefix = CommandPrefix(input);
        if (commandPrefix.Length == 0)
        {
            return [];
        }

        return commandDispatcher.RegisteredNamesSnapshot()
            .Where(name => name.StartsWith(commandPrefix, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(name => new CandidateSpec(
                CandidateKind.Command,
                CandidateIconKind.Command,
                name,
                options.CommandDescription,
                ReplaceCommandPrefix(input, name),
                null,
                $"command:{name}"))
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
            candidates[index] = new InputCandidate(
                token,
                spec.Kind,
                spec.IconKind,
                spec.PrimaryText,
                spec.SecondaryText);
            targets.Add(token, new CandidateTarget(spec.Kind, spec.Value, spec.EntryKind));
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
        try
        {
            nativeShell.SetInputCandidates(candidates, revision);
        }
        catch
        {
            Volatile.Write(ref activeTargets, previousTargets);
            throw;
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
                ? fileLauncher.Reveal(fullPath, entryKind)
                : fileLauncher.Open(fullPath, entryKind);
            if (started)
            {
                nativeShell.HideCommandInput();
                return;
            }

            ReportStatus($"The indexed item is no longer available: {fullPath}");
        }
        catch (Exception exception)
        {
            ReportStatus($"Cannot activate indexed item: {exception.Message}");
        }
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

    private static string CommandPrefix(string input)
    {
        var trimmed = input.AsSpan().TrimStart();
        var separator = IndexOfWhitespace(trimmed);
        return (separator < 0 ? trimmed : trimmed[..separator]).ToString();
    }

    private static string ReplaceCommandPrefix(string input, string commandName)
    {
        var trimmed = input.AsSpan().Trim();
        var separator = IndexOfWhitespace(trimmed);
        return separator < 0
            ? commandName
            : string.Concat(commandName, trimmed[separator..]);
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

    private sealed record CandidateSpec(
        CandidateKind Kind,
        CandidateIconKind IconKind,
        string PrimaryText,
        string SecondaryText,
        string Value,
        FileSystemEntryKind? EntryKind,
        string Identity);

    private sealed record CandidateTarget(
        CandidateKind Kind,
        string Value,
        FileSystemEntryKind? EntryKind);
}
