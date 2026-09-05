using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LuvLetter.Core.Application;
using LuvLetter.Core.Modules.Indexing;
using LuvLetter.Platform.Diagnostics;
using LuvLetter.Platform.Indexing;
using Microsoft.Extensions.Hosting;

namespace LuvLetter.Platform.Applications;

internal sealed class WindowsApplicationCatalog : IApplicationCatalog, IHostedService, IDisposable
{
    private const int MaximumEntriesPerPartition = 20_000;
    private const int MaximumPublishedEntries = 100_000;
    private const int MaximumCooldownEntries = 4096;
    private const int MaximumConcurrentDiscoveries = 4;
    private const long WatcherRetryMilliseconds = 5000;
    private static readonly string[] BuiltInSources =
    [
        "start-menu:user",
        "start-menu:common",
        "app-paths:user",
        "app-paths:machine",
        "apps-folder",
        "system:curated",
    ];

    private readonly WindowsApplicationDiscovery discovery;
    private readonly ApplicationPartitionCache cache;
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly SemaphoreSlim cacheSlots = new(2, 2);
    private readonly Dictionary<string, ApplicationPartition> partitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WatcherRegistration> watchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> watcherRetries = new(StringComparer.Ordinal);
    private readonly HashSet<Task> activeJobs = [];
    private readonly string[] fullIgnorePaths;
    private readonly string[] ignoreRebuildPaths;
    private readonly HashSet<string> ignoreRebuildNames;
    private readonly long refreshMilliseconds;
    private readonly long cooldownMilliseconds;
    private CancellationTokenSource? lifetime;
    private Task? worker;
    private PublishedCatalog published = PublishedCatalog.Empty;
    private bool unavailable;
    private bool stopping;
    private bool started;
    private bool forceAllPending;
    private long nextOwnershipEpoch;
    private long publicationRevision;
    private static readonly IComparer<ScoredApplication> BestMatchFirst =
        Comparer<ScoredApplication>.Create(static (left, right) => CompareMatchQuality(right, left));

    public WindowsApplicationCatalog(FileIndexClientOptions fileOptions, WindowsApplicationDiscovery discovery)
    {
        this.discovery = discovery;
        cache = new(ApplicationCatalogOptions.DataDirectory);
        fullIgnorePaths = fileOptions.Maintenance.NormalizedFullIgnorePaths();
        ignoreRebuildPaths = fileOptions.Maintenance.NormalizedIgnoreDirectories();
        ignoreRebuildNames = new(fileOptions.Maintenance.NormalizedIgnoreDirectoryNames(), StringComparer.OrdinalIgnoreCase);
        refreshMilliseconds = (long)fileOptions.Maintenance.RefreshIntervalSeconds * 1000;
        cooldownMilliseconds = (long)fileOptions.Maintenance.TriggerCooldownSeconds * 1000;
        unavailable = !fileOptions.Maintenance.IsAvailable;
    }

    public event Action? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (started) throw new InvalidOperationException("The application catalog has already started.");
            started = true;
            if (unavailable) return Task.CompletedTask;
            lifetime = new CancellationTokenSource();
            worker = Task.Run(() => WorkerAsync(lifetime.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? currentWorker;
        lock (gate)
        {
            stopping = true;
            lifetime?.Cancel();
            currentWorker = worker;
        }
        DisposeWatchers();
        if (currentWorker is null) return;
        try { await currentWorker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { Log("shutdown", "Application catalog shutdown continuing in background"); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            stopping = true;
            lifetime?.Cancel();
        }
        DisposeWatchers();
        // A Shell/COM enumeration can outlive bounded host shutdown. Keep its token and wake handle valid.
    }

    public void RequestRefresh() => RequestRefresh(IndexRefreshMode.Normal);

    public void RequestRefresh(IndexRefreshMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        string[] queued;
        lock (gate)
        {
            if (stopping || unavailable)
            {
                Log("configuration-error", "Application refresh unavailable | state=stopped-or-invalid-configuration");
                return;
            }
            if (partitions.Count == 0)
            {
                forceAllPending |= mode == IndexRefreshMode.Force;
                queued = [];
            }
            else
            {
                queued = partitions.Values.Select(partition =>
                {
                    if (mode == IndexRefreshMode.Force)
                    {
                        return ForceLocked(partition);
                    }
                    MarkDirtyLocked(partition, "manual", force: false);
                    return partition.SourceId;
                }).ToArray();
            }
        }
        var eventName = mode == IndexRefreshMode.Force ? "force" : "manual";
        if (queued.Length == 0)
            Log(eventName, mode == IndexRefreshMode.Force
                ? "Force rebuild queued | catalog=applications | partition=all-pending-startup | cooldown=bypassed"
                : "Reconciliation queued | catalog=applications | partition=all-pending-startup");
        else if (ConsoleLog.IsEnabled)
            foreach (var sourceId in queued)
                Log(eventName, mode == IndexRefreshMode.Force
                    ? $"Force rebuild queued | catalog=applications | partition={SafeMessage(sourceId)} | cooldown=bypassed"
                    : $"Reconciliation queued | catalog=applications | partition={SafeMessage(sourceId)}");
        Wake();
    }

    internal bool RequestRefresh(string partitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionId);
        lock (gate)
        {
            if (stopping || unavailable || !partitions.TryGetValue(partitionId, out var partition)) return false;
            ForceLocked(partition);
        }
        Log("force", $"Force rebuild queued | catalog=applications | partition={SafeMessage(partitionId)} | cooldown=bypassed");
        Wake();
        return true;
    }

    public IReadOnlyList<ApplicationMatch> Query(string query, int maximumResults)
    {
        if (string.IsNullOrWhiteSpace(query) || maximumResults <= 0) return [];
        var preparedQuery = ApplicationNameMatcher.CreateQuery(query);
        if (!preparedQuery.IsEligible) return [];
        var current = Volatile.Read(ref published);
        var limit = Math.Min(maximumResults, 256);
        var best = new ScoredApplication[limit];
        var count = 0;
        foreach (var candidate in current.SearchEntries)
        {
            var score = ApplicationNameMatcher.Score(candidate.Entry, candidate.NameIndex, preparedQuery);
            if (!score.HasValue) continue;
            var scored = new ScoredApplication(candidate.Entry, score.Value);
            if (count < limit)
            {
                best[count] = scored;
                SiftUpWorstFirst(best, count++);
            }
            else if (CompareMatchQuality(scored, best[0]) > 0)
            {
                best[0] = scored;
                SiftDownWorstFirst(best, count, 0);
            }
        }
        if (count == 0) return [];
        Array.Sort(best, 0, count, BestMatchFirst);
        var matches = new ApplicationMatch[count];
        for (var index = 0; index < count; index++)
            matches[index] = new(best[index].Entry, best[index].Score);
        return matches;
    }

    public bool TryGet(string id, out ApplicationEntry? entry)
    {
        var current = Volatile.Read(ref published);
        if (!string.IsNullOrEmpty(id) && current.ById.TryGetValue(id, out var candidate) && IsEntryAllowed(candidate))
        {
            entry = candidate;
            return true;
        }
        entry = null;
        return false;
    }

    internal bool IsPathExcluded(string path)
    {
        if (string.IsNullOrEmpty(path) || fullIgnorePaths.Length == 0) return false;
        try
        {
            var normalized = FileIndexMaintenanceOptions.NormalizeScopePath(path);
            return IsNormalizedPathExcluded(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return true;
        }
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = await ApplicationCatalogOptions.LoadAsync(ApplicationCatalogOptions.DataDirectory, cancellationToken)
                .ConfigureAwait(false);
            var definitions = CreateDefinitions(options.NormalizedRoots());
            lock (gate)
            {
                if (stopping) return;
                var now = Environment.TickCount64;
                foreach (var definition in definitions)
                {
                    var partition = new ApplicationPartition(definition, ++nextOwnershipEpoch,
                        ScopeFingerprint(definition), now + refreshMilliseconds);
                    if (forceAllPending) ForceLocked(partition);
                    partitions.Add(partition.SourceId, partition);
                }
                forceAllPending = false;
            }

            foreach (var definition in definitions)
            {
                ApplicationPartition partition;
                lock (gate) partition = partitions[definition.SourceId];
                TrackJob(LoadPartitionAsync(partition.SourceId, partition.OwnershipEpoch, cancellationToken));
            }

            await SchedulerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (gate) unavailable = true;
            Log("configuration-error", $"Application catalog paused | reason={SafeMessage(exception.Message)}");
        }
        finally
        {
            DisposeWatchers();
            Task[] jobs;
            lock (gate) jobs = activeJobs.ToArray();
            if (jobs.Length != 0)
            {
                try { await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception) { /* Shutdown remains bounded; tracked tasks observe their own failures. */ }
            }
        }
    }

    private async Task SchedulerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            EnsureWatchers();
            List<RefreshTicket> refreshes = [];
            List<string> periodic = [];
            long waitMilliseconds;
            lock (gate)
            {
                var now = Environment.TickCount64;
                var runningTotal = partitions.Values.Count(partition => partition.Running);
                var runningByCategory = partitions.Values.Where(partition => partition.Running)
                    .GroupBy(partition => partition.Category)
                    .ToDictionary(group => group.Key, group => group.Count());
                foreach (var partition in partitions.Values)
                {
                    if (now >= partition.NextPeriodicAt)
                    {
                        partition.NextPeriodicAt += ((now - partition.NextPeriodicAt) / refreshMilliseconds + 1)
                            * refreshMilliseconds;
                        MarkDirtyLocked(partition, "periodic", force: false);
                        periodic.Add(partition.SourceId);
                    }
                    if (partition.CacheLoaded && partition.Dirty && !partition.Running
                        && (partition.ForceRequested || now >= partition.NextAllowedAt)
                        && runningTotal < MaximumConcurrentDiscoveries
                        && runningByCategory.GetValueOrDefault(partition.Category) < CategoryLimit(partition.Category))
                    {
                        partition.Running = true;
                        partition.State = "refreshing";
                        var cause = partition.ForceRequested ? "force" : partition.PendingCause;
                        partition.ForceRequested = false;
                        // This job owns the current dirty revision. A later trigger marks the
                        // partition dirty again and is scheduled after this job commits.
                        partition.Dirty = false;
                        refreshes.Add(new(partition.SourceId, partition.PortableRoot,
                            partition.OwnershipEpoch, cause));
                        runningTotal++;
                        runningByCategory[partition.Category] = runningByCategory.GetValueOrDefault(partition.Category) + 1;
                    }
                }

                waitMilliseconds = refreshMilliseconds;
                foreach (var partition in partitions.Values)
                {
                    waitMilliseconds = Math.Min(waitMilliseconds, Math.Max(1, partition.NextPeriodicAt - now));
                    if (partition.CacheLoaded && partition.Dirty && !partition.Running && !partition.ForceRequested)
                    {
                        var untilEligible = partition.NextAllowedAt - now;
                        // A completed job wakes the scheduler. This finite wait is a fallback
                        // when a category has exhausted its discovery slots.
                        waitMilliseconds = Math.Min(waitMilliseconds,
                            untilEligible > 0 ? untilEligible : 1000);
                    }
                }
                if (watcherRetries.Count != 0)
                    waitMilliseconds = Math.Min(waitMilliseconds, Math.Max(1, watcherRetries.Values.Min() - now));
            }

            foreach (var sourceId in periodic)
                Log("periodic", $"Automatic index rebuild | catalog=applications | partition={SafeMessage(sourceId)} | result=queued");
            foreach (var refresh in refreshes)
            {
                if (ConsoleLog.IsEnabled)
                    LogPartition("applications", refresh.PartitionId, refresh.Cause, 0, "refresh-started");
                TrackJob(RefreshPartitionAsync(refresh, cancellationToken));
            }
            if (refreshes.Count != 0) continue;
            await wake.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, waitMilliseconds)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task LoadPartitionAsync(string partitionId, long ownershipEpoch, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        ApplicationPartitionCacheLoadResult result;
        try
        {
            string fingerprint;
            lock (gate)
            {
                if (!CurrentPartitionLocked(partitionId, ownershipEpoch, out var current)) return;
                fingerprint = current.ScopeFingerprint;
            }
            await cacheSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                result = await cache.LoadAsync(partitionId, fingerprint, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                cacheSlots.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            result = new(null, null, "error:" + SafeMessage(exception.Message));
        }

        PendingPublication? publication = null;
        var cacheResult = result.Result;
        lock (gate)
        {
            if (!CurrentPartitionLocked(partitionId, ownershipEpoch, out var partition)) return;
            partition.CacheLoaded = true;
            var snapshot = result.Snapshot;
            if (snapshot is not null && ValidateEntries(snapshot.Entries, partition)
                && (fullIgnorePaths.Length == 0 || snapshot.Entries.All(IsEntryAllowed)))
            {
                Array.Sort(snapshot.Entries, ApplicationEntryIdComparer.Instance);
                partition.Entries = snapshot.Entries;
                partition.Generation = snapshot.Generation;
                partition.TrustedCache = result.Generation;
                partition.HasUsableSnapshot = true;
                partition.Availability = "unknown";
                partition.Freshness = "stale";
                partition.State = "dirty";
                publication = PreparePublicationLocked();
            }
            else if (snapshot is not null)
            {
                cacheResult = "incompatible";
                partition.Availability = "unknown";
                partition.Freshness = "unavailable";
                partition.State = "dirty";
            }
            else
            {
                partition.State = "dirty";
            }
        }
        if (publication is not null) Publish(publication);
        if (ConsoleLog.IsEnabled)
            LogPartition("applications", partitionId, "cache", stopwatch.ElapsedMilliseconds,
                "cache-" + SafeMessage(cacheResult));
        Wake();
    }

    private async Task RefreshPartitionAsync(RefreshTicket ticket, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        ApplicationDiscoveryResult result;
        try
        {
            result = await discovery.DiscoverSourceAsync(ticket.PartitionId, ticket.PortableRoot,
                IsPathExcluded, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteCancelledRefresh(ticket);
            return;
        }
        catch (Exception exception)
        {
            CompleteFailedRefresh(ticket, stopwatch.ElapsedMilliseconds, exception.Message);
            return;
        }

        ApplicationPartition validationPartition;
        lock (gate)
        {
            if (!CurrentPartitionLocked(ticket.PartitionId, ticket.OwnershipEpoch, out validationPartition)
                || !validationPartition.Running) return;
        }
        if (!result.Succeeded || result.SourceId != ticket.PartitionId
            || !ValidateEntries(result.Entries, validationPartition))
        {
            var reason = result.SourceId != ticket.PartitionId ? "mismatched-source-result"
                : result.Error ?? "invalid-source-entries";
            CompleteFailedRefresh(ticket, stopwatch.ElapsedMilliseconds, reason);
            return;
        }

        var entries = fullIgnorePaths.Length == 0 ? result.Entries : result.Entries.Where(IsEntryAllowed).ToArray();
        Array.Sort(entries, ApplicationEntryIdComparer.Instance);
        ApplicationPartitionSnapshot? snapshot = null;
        ApplicationPartitionCacheGeneration? previous;
        PendingPublication? publication = null;
        long generation = 0;
        var unchanged = false;
        lock (gate)
        {
            if (!CurrentPartitionLocked(ticket.PartitionId, ticket.OwnershipEpoch, out var partition)
                || !partition.Running) return;
            if (partition.HasUsableSnapshot && EntriesEqual(partition.Entries, entries))
            {
                partition.Running = false;
                partition.FailureCount = 0;
                partition.Availability = "available";
                partition.Freshness = "fresh";
                partition.State = partition.Dirty ? "dirty" : "ready";
                partition.NextAllowedAt = Environment.TickCount64 + cooldownMilliseconds;
                unchanged = true;
            }
            if (unchanged)
            {
                previous = null;
                generation = partition.Generation;
            }
            else
            {
                partition.Entries = entries;
                partition.Generation = partition.Generation >= long.MaxValue - 1
                    ? 1 : partition.Generation + 1;
                generation = partition.Generation;
                partition.HasUsableSnapshot = true;
                partition.FailureCount = 0;
                partition.Availability = "available";
                partition.Freshness = "fresh";
                partition.State = "persisting";
                partition.NextAllowedAt = Environment.TickCount64 + cooldownMilliseconds;
                snapshot = ApplicationPartitionCache.CreateSnapshot(partition.SourceId,
                    partition.ScopeFingerprint, partition.Generation, partition.Entries);
                previous = partition.TrustedCache;
                publication = PreparePublicationLocked();
            }
        }

        if (unchanged)
        {
            if (ConsoleLog.IsEnabled)
                LogPartition("applications", ticket.PartitionId, ticket.Cause, stopwatch.ElapsedMilliseconds,
                    "refresh-unchanged");
            Wake();
            return;
        }

        // Query readers see this source as soon as discovery commits; cache I/O remains partition-local.
        Publish(publication!);
        var persisted = false;
        string? persistenceError = null;
        ApplicationPartitionCacheGeneration? saved = null;
        try
        {
            await cacheSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                saved = await cache.SaveAsync(snapshot!, previous, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                cacheSlots.Release();
            }
            persisted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            persistenceError = exception.Message;
        }

        lock (gate)
        {
            if (CurrentPartitionLocked(ticket.PartitionId, ticket.OwnershipEpoch, out var partition)
                && partition.Running && partition.Generation == generation)
            {
                if (saved is not null) partition.TrustedCache = saved;
                partition.Running = false;
                partition.State = partition.Dirty ? "dirty" : "ready";
            }
        }
        if (ConsoleLog.IsEnabled)
            LogPartition("applications", ticket.PartitionId, ticket.Cause, stopwatch.ElapsedMilliseconds,
                persisted ? "refresh-committed" : "refresh-committed-cache-unavailable", persistenceError);
        Wake();
    }

    private void CompleteFailedRefresh(RefreshTicket ticket, long durationMilliseconds, string reason)
    {
        lock (gate)
        {
            if (!CurrentPartitionLocked(ticket.PartitionId, ticket.OwnershipEpoch, out var partition)
                || !partition.Running) return;
            partition.Running = false;
            partition.FailureCount = Math.Min(partition.FailureCount + 1, 16);
            var retryMilliseconds = RetryDelayMilliseconds(partition.FailureCount);
            partition.NextAllowedAt = Environment.TickCount64 + retryMilliseconds;
            if (!partition.Dirty)
            {
                partition.Dirty = true;
                partition.DirtyRevision++;
                partition.PendingCause = "retry";
            }
            partition.Availability = "unavailable";
            partition.Freshness = partition.HasUsableSnapshot ? "stale" : "unavailable";
            partition.State = partition.HasUsableSnapshot ? "stale-retry" : "failed-retry";
        }
        if (ConsoleLog.IsEnabled)
            LogPartition("applications", ticket.PartitionId, ticket.Cause, durationMilliseconds,
                "refresh-failed-previous-snapshot-retained", reason);
        Wake();
    }

    private void CompleteCancelledRefresh(RefreshTicket ticket)
    {
        lock (gate)
        {
            if (CurrentPartitionLocked(ticket.PartitionId, ticket.OwnershipEpoch, out var partition))
                partition.Running = false;
        }
    }

    private void Publish(PendingPublication pendingPublication)
    {
        IEnumerable<ApplicationEntry> candidates = pendingPublication.Sources.SelectMany(source => source.Entries);
        if (fullIgnorePaths.Length != 0) candidates = candidates.Where(IsEntryAllowed);
        var entries = candidates
            .GroupBy(entry => string.IsNullOrEmpty(entry.DeduplicationKey) ? entry.Id : entry.DeduplicationKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var chosen = group.OrderBy(entry => LaunchPriority(entry.LaunchKind))
                    .ThenBy(entry => SourcePriority(entry.Source))
                    .ThenBy(entry => entry.Id, StringComparer.Ordinal).First();
                return chosen with
                {
                    Aliases = group.SelectMany(entry => entry.Aliases.Append(entry.DisplayName))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray(),
                };
            })
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.DisplayName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .Take(MaximumPublishedEntries).ToArray();
        var current = Volatile.Read(ref published);
        if (EntriesEqual(current.SearchEntries, entries)) return;
        var searchEntries = new PublishedSearchEntry[entries.Length];
        for (var index = 0; index < entries.Length; index++)
            searchEntries[index] = new(entries[index], ApplicationNameMatcher.CreateIndex(entries[index]));
        var byId = new Dictionary<string, ApplicationEntry>(StringComparer.Ordinal);
        foreach (var entry in entries) byId.TryAdd(entry.Id, entry);
        lock (gate)
        {
            if (stopping || pendingPublication.Revision != publicationRevision) return;
            Volatile.Write(ref published, new(searchEntries, byId));
        }
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* An observer cannot terminate maintenance. */ }
    }

    private PendingPublication PreparePublicationLocked()
    {
        var sources = partitions.Values.Where(partition => partition.HasUsableSnapshot)
            .Select(partition => new PublishedSource(partition.SourceId, partition.Entries)).ToArray();
        return new(++publicationRevision, sources);
    }

    private bool ValidateEntries(ApplicationEntry[]? entries, ApplicationPartition partition)
    {
        if (entries is null || entries.Length > MaximumEntriesPerPartition) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return entries.All(entry => entry is not null && entry.Source == partition.SourceId && ids.Add(entry.Id)
            && ValidText(entry.Id, 65536) && ValidText(entry.DisplayName, 512)
            && entry.Id.StartsWith(partition.SourceId + ":", StringComparison.Ordinal)
            && entry.Aliases is { Length: <= 64 } && entry.Aliases.All(alias => ValidText(alias, 512))
            && Enum.IsDefined(entry.LaunchKind) && ValidText(entry.LaunchTarget, 32767)
            && (entry.Arguments is null || entry.Arguments.Length <= 32767 && !entry.Arguments.Contains('\0'))
            && (entry.DeduplicationKey is null || ValidText(entry.DeduplicationKey, 65536))
            && OptionalPath(entry.ExecutablePath) && OptionalPath(entry.WorkingDirectory)
            && OptionalPath(entry.InstallDirectory)
            && (entry.SearchPath is null || entry.SearchPath.Length <= 65536
                && entry.SearchPath.Split(';', StringSplitOptions.RemoveEmptyEntries).All(ValidPath))
            && ValidLaunchTarget(entry) && EntryMatchesSource(entry, partition));
    }

    private static bool ValidLaunchTarget(ApplicationEntry entry) => entry.LaunchKind switch
    {
        ApplicationLaunchKind.Shortcut => ValidPath(entry.LaunchTarget)
            && entry.LaunchTarget.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase),
        ApplicationLaunchKind.Executable => ValidPath(entry.LaunchTarget)
            && entry.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
        ApplicationLaunchKind.RegisteredExecutable => ValidPath(entry.ExecutablePath)
            && entry.LaunchTarget.IndexOfAny(['\\', '/', ':']) < 0
            && entry.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
        ApplicationLaunchKind.Packaged => entry.LaunchTarget.Contains('!')
            && entry.LaunchTarget.IndexOfAny(['\\', '/']) < 0,
        ApplicationLaunchKind.ShellItem or ApplicationLaunchKind.SettingsUri or ApplicationLaunchKind.ControlPanel =>
            WindowsApplicationDiscovery.TryValidateSpecialEntry(entry),
        _ => false,
    };

    private static bool EntryMatchesSource(ApplicationEntry entry, ApplicationPartition partition)
    {
        if (partition.SourceId is "app-paths:user" or "app-paths:machine")
            return entry.LaunchKind == ApplicationLaunchKind.RegisteredExecutable;
        if (partition.SourceId == "apps-folder")
            return entry.LaunchKind is ApplicationLaunchKind.Packaged
                || entry.LaunchKind == ApplicationLaunchKind.ShellItem
                    && WindowsApplicationDiscovery.TryValidateSpecialEntry(entry);
        if (partition.SourceId == "system:curated")
            return entry.LaunchKind is ApplicationLaunchKind.Executable
                    or ApplicationLaunchKind.ShellItem or ApplicationLaunchKind.SettingsUri
                    or ApplicationLaunchKind.ControlPanel
                && WindowsApplicationDiscovery.TryValidateSpecialEntry(entry);
        if (string.IsNullOrEmpty(partition.ScopeRoot)) return false;
        try
        {
            return entry.LaunchKind == (partition.PortableRoot is null
                    ? ApplicationLaunchKind.Shortcut : ApplicationLaunchKind.Executable)
                && SameOrChild(partition.ScopeRoot,
                    FileIndexMaintenanceOptions.NormalizeScopePath(entry.LaunchTarget));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private bool IsEntryAllowed(ApplicationEntry entry)
    {
        if (entry.LaunchKind is ApplicationLaunchKind.Shortcut or ApplicationLaunchKind.Executable
            && IsPathExcluded(entry.LaunchTarget)) return false;
        if (!string.IsNullOrEmpty(entry.ExecutablePath) && IsPathExcluded(entry.ExecutablePath)
            || !string.IsNullOrEmpty(entry.WorkingDirectory) && IsPathExcluded(entry.WorkingDirectory)
            || !string.IsNullOrEmpty(entry.InstallDirectory) && IsPathExcluded(entry.InstallDirectory)) return false;
        // App Paths search directories augment PATH; they are not the application target.
        return true;
    }

    private void EnsureWatchers()
    {
        ApplicationPartition[] watchable;
        lock (gate) watchable = partitions.Values.Where(partition => !string.IsNullOrEmpty(partition.WatchRoot)).ToArray();
        foreach (var partition in watchable)
        {
            var root = partition.WatchRoot!;
            if (IsPathExcluded(root)) continue;
            lock (gate)
            {
                if (stopping || watchers.ContainsKey(partition.SourceId)
                    || watcherRetries.TryGetValue(partition.SourceId, out var retryAt)
                        && Environment.TickCount64 < retryAt) continue;
            }
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                var sourceId = partition.SourceId;
                var epoch = partition.OwnershipEpoch;
                watcher.Created += (_, change) => QueueFileChange(sourceId, epoch, change.FullPath);
                watcher.Changed += (_, change) => QueueFileChange(sourceId, epoch, change.FullPath);
                watcher.Deleted += (_, change) => QueueFileChange(sourceId, epoch, change.FullPath);
                watcher.Renamed += (_, change) =>
                {
                    QueueFileChange(sourceId, epoch, change.OldFullPath);
                    QueueFileChange(sourceId, epoch, change.FullPath);
                };
                watcher.Error += (sender, error) =>
                {
                    if (sender is FileSystemWatcher failed)
                        RecoverWatcher(sourceId, epoch, root, failed, error.GetException());
                };
                lock (gate)
                {
                    if (stopping || !CurrentPartitionLocked(sourceId, epoch, out _))
                    {
                        watcher.Dispose();
                        continue;
                    }
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(sourceId, new(sourceId, epoch, root, watcher));
                    if (watcherRetries.Remove(sourceId))
                        Log("watcher-recovery", $"Application watcher reopened | partition={SafeMessage(sourceId)} | path={SafeMessage(root)}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                lock (gate)
                {
                    if (stopping) continue;
                    if (!watcherRetries.ContainsKey(partition.SourceId))
                        Log("watcher-recovery", $"Application watcher unavailable | partition={SafeMessage(partition.SourceId)} | path={SafeMessage(root)} | retry_after_seconds=5 | fallback=periodic-discovery | reason={SafeMessage(exception.Message)}");
                    watcherRetries[partition.SourceId] = Environment.TickCount64 + WatcherRetryMilliseconds;
                }
            }
        }
    }

    private void RecoverWatcher(string partitionId, long ownershipEpoch, string root,
        FileSystemWatcher failed, Exception error)
    {
        lock (gate)
        {
            if (stopping || !CurrentPartitionLocked(partitionId, ownershipEpoch, out _)
                || !watchers.TryGetValue(partitionId, out var current)
                || !ReferenceEquals(current.Watcher, failed)) return;
            watchers.Remove(partitionId);
            watcherRetries[partitionId] = Environment.TickCount64 + WatcherRetryMilliseconds;
        }
        failed.Dispose();
        Log("watcher-recovery", $"Application watcher removed after error | partition={SafeMessage(partitionId)} | path={SafeMessage(root)} | result=reopen-pending | retry_after_seconds=5 | reason={SafeMessage(error.Message)}");
        QueueFileChange(partitionId, ownershipEpoch, root, recovery: true);
        Wake();
    }

    private void QueueFileChange(string partitionId, long ownershipEpoch, string path, bool recovery = false)
    {
        // Full ignore removes the entry and its trigger; ordinary ignore only removes this trigger.
        try { path = FileIndexMaintenanceOptions.NormalizeScopePath(path); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return; }
        if (IsNormalizedPathExcluded(path)) return;
        if (!recovery && (ignoreRebuildPaths.Any(parent => SameOrChild(parent, path))
            || path[(Path.GetPathRoot(path)?.Length ?? 0)..].Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .Any(ignoreRebuildNames.Contains))) return;

        string? refusal = null;
        string? queued = null;
        long remainingSeconds = 0;
        lock (gate)
        {
            if (stopping || unavailable || !CurrentPartitionLocked(partitionId, ownershipEpoch, out var partition)) return;
            var now = Environment.TickCount64;
            foreach (var key in partition.TriggerTimes.Where(pair => now - pair.Value >= cooldownMilliseconds)
                .Select(pair => pair.Key).ToArray()) partition.TriggerTimes.Remove(key);
            if (partition.TriggerTimes.TryGetValue(path, out var triggeredAt))
            {
                remainingSeconds = (Math.Max(0, triggeredAt + cooldownMilliseconds - now) + 999) / 1000;
                refusal = "cooldown";
            }
            else if (partition.TriggerTimes.Count >= MaximumCooldownEntries)
            {
                refusal = "capacity";
            }
            else
            {
                partition.TriggerTimes[path] = now;
                var wasDirty = partition.Dirty;
                MarkDirtyLocked(partition, recovery ? "watcher-recovery" : "file-change", force: false);
                remainingSeconds = partition.ForceRequested ? 0
                    : partition.Running ? (cooldownMilliseconds + 999) / 1000
                    : (Math.Max(0, partition.NextAllowedAt - now) + 999) / 1000;
                queued = wasDirty ? "coalesced" : "queued";
            }
        }

        if (ConsoleLog.IsEnabled)
        {
            if (refusal == "cooldown")
                Log(recovery ? "watcher-recovery" : "cooldown-refused", (recovery
                        ? "Application watcher recovery cooldown refused" : "File changed but cooldown refused")
                    + $" | catalog=applications | partition={SafeMessage(partitionId)} | path={SafeMessage(path)} | remaining_seconds={remainingSeconds}");
            else if (refusal == "capacity")
                Log("capacity-refused", $"Application rebuild request refused | catalog=applications | partition={SafeMessage(partitionId)} | reason=cooldown-map-capacity | path={SafeMessage(path)} | capacity={MaximumCooldownEntries} | fallback=periodic-discovery");
            else
                Log(recovery ? "watcher-recovery" : "file-change", recovery
                    ? $"Application watcher recovery queued | partition={SafeMessage(partitionId)} | path={SafeMessage(path)} | minimum_wait_seconds={remainingSeconds}"
                    : $"File changed triggered | catalog=applications | partition={SafeMessage(partitionId)} | result={queued} | path={SafeMessage(path)} | minimum_wait_seconds={remainingSeconds}");
        }
        if (queued is not null) Wake();
    }

    private void DisposeWatchers()
    {
        FileSystemWatcher[] current;
        lock (gate)
        {
            current = watchers.Values.Select(registration => registration.Watcher).ToArray();
            watchers.Clear();
            watcherRetries.Clear();
        }
        foreach (var watcher in current) watcher.Dispose();
    }

    private void TrackJob(Task task)
    {
        lock (gate) activeJobs.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (gate) activeJobs.Remove(completed);
            Wake();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private string ForceLocked(ApplicationPartition partition)
    {
        MarkDirtyLocked(partition, "force", force: true);
        return partition.SourceId;
    }

    private static void MarkDirtyLocked(ApplicationPartition partition, string cause, bool force)
    {
        var wasDirty = partition.Dirty;
        partition.Dirty = true;
        partition.DirtyRevision++;
        if (force)
        {
            partition.ForceRequested = true;
            partition.PendingCause = "force";
        }
        else if (!partition.ForceRequested && !wasDirty)
        {
            partition.PendingCause = cause;
        }
        if (!partition.Running) partition.State = "dirty";
    }

    private bool CurrentPartitionLocked(string partitionId, long ownershipEpoch,
        out ApplicationPartition partition) =>
        partitions.TryGetValue(partitionId, out partition!) && partition.OwnershipEpoch == ownershipEpoch;

    private void LogPartition(string eventName, string partitionId, string cause,
        long durationMilliseconds, string result, string? reason = null)
    {
        if (!ConsoleLog.IsEnabled) return;
        string message;
        lock (gate)
        {
            if (!partitions.TryGetValue(partitionId, out var partition)) return;
            message = $"Application partition | partition={SafeMessage(partition.SourceId)} | cause={SafeMessage(cause)}"
                + $" | duration_ms={Math.Max(0, durationMilliseconds)} | entries={partition.Entries.Length}"
                + $" | availability={partition.Availability} | freshness={partition.Freshness}"
                + $" | state={partition.State} | dirty={partition.Dirty.ToString().ToLowerInvariant()}"
                + $" | generation={partition.Generation}"
                + $" | retry_after_seconds={(Math.Max(0, partition.NextAllowedAt - Environment.TickCount64) + 999) / 1000}"
                + $" | result={SafeMessage(result)}";
        }
        if (!string.IsNullOrEmpty(reason)) message += " | reason=" + SafeMessage(reason);
        Log(eventName, message);
    }

    private ApplicationPartitionDefinition[] CreateDefinitions(string[] portableRoots)
    {
        var userPrograms = NormalizeKnownRoot(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        var commonPrograms = NormalizeKnownRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        var definitions = new List<ApplicationPartitionDefinition>
        {
            new("start-menu:user", null, userPrograms, userPrograms),
            new("start-menu:common", null, commonPrograms, commonPrograms),
            new("app-paths:user", null, null, null),
            new("app-paths:machine", null, null, null),
            new("apps-folder", null, null, null),
            new("system:curated", null, null, null),
        };
        definitions.AddRange(portableRoots.Select(root =>
            new ApplicationPartitionDefinition(WindowsApplicationDiscovery.PortableSourceId(root), root, root, root)));
        if (definitions.Select(definition => definition.SourceId).Distinct(StringComparer.Ordinal).Count()
            != BuiltInSources.Length + portableRoots.Length)
            throw new InvalidDataException("Application source identifiers must be unique.");
        return definitions.ToArray();
    }

    private string ScopeFingerprint(ApplicationPartitionDefinition definition)
    {
        var builder = new StringBuilder();
        builder.Append("application-partition-v2\n").Append(definition.SourceId).Append('\n')
            .Append(definition.ScopeRoot?.ToUpperInvariant() ?? string.Empty).Append('\n');
        foreach (var path in fullIgnorePaths.Order(StringComparer.OrdinalIgnoreCase))
            builder.Append(path.ToUpperInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string? NormalizeKnownRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return FileIndexMaintenanceOptions.NormalizeScopePath(path); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private void Wake()
    {
        try { wake.Release(); } catch (SemaphoreFullException) { }
    }

    private static int LaunchPriority(ApplicationLaunchKind kind) => kind switch
    {
        ApplicationLaunchKind.Shortcut => 0,
        ApplicationLaunchKind.RegisteredExecutable => 1,
        ApplicationLaunchKind.Packaged => 2,
        ApplicationLaunchKind.Executable => 3,
        _ => 4,
    };

    private static int SourcePriority(string source) => source switch
    {
        "start-menu:user" => 0,
        "start-menu:common" => 1,
        "app-paths:user" => 2,
        "app-paths:machine" => 3,
        "apps-folder" => 4,
        "system:curated" => 5,
        _ => 6,
    };

    private static int CategoryLimit(string category) => category switch
    {
        "registry" => 1,
        "portable" => 2,
        _ => 2,
    };

    private long RetryDelayMilliseconds(int failureCount)
    {
        var multiplier = 1L << Math.Min(Math.Max(0, failureCount - 1), 20);
        return Math.Min(Math.Max(refreshMilliseconds, cooldownMilliseconds),
            checked(cooldownMilliseconds * multiplier));
    }

    private static bool ValidText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static bool OptionalPath(string? path) => string.IsNullOrEmpty(path) || ValidPath(path);

    private static bool ValidPath(string? path) => ValidText(path, 32767) && Path.IsPathFullyQualified(path!);

    private static bool SameOrChild(string parent, string path) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private bool IsNormalizedPathExcluded(string normalizedPath)
    {
        if (fullIgnorePaths.Length == 0) return false;
        foreach (var parent in fullIgnorePaths)
            if (SameOrChild(parent, normalizedPath)) return true;
        return false;
    }

    private static bool EntriesEqual(ApplicationEntry[] left, ApplicationEntry[] right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!EntryEquals(left[index], right[index])) return false;
        return true;
    }

    private static bool EntriesEqual(PublishedSearchEntry[] left, ApplicationEntry[] right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!EntryEquals(left[index].Entry, right[index])) return false;
        return true;
    }

    private static bool EntryEquals(ApplicationEntry left, ApplicationEntry right) =>
        left.Id == right.Id
        && left.DisplayName == right.DisplayName
        && left.Aliases.AsSpan().SequenceEqual(right.Aliases)
        && left.LaunchKind == right.LaunchKind
        && left.LaunchTarget == right.LaunchTarget
        && left.ExecutablePath == right.ExecutablePath
        && left.WorkingDirectory == right.WorkingDirectory
        && left.Arguments == right.Arguments
        && left.Source == right.Source
        && left.DeduplicationKey == right.DeduplicationKey
        && left.InstallDirectory == right.InstallDirectory
        && left.SearchPath == right.SearchPath;

    private static int CompareMatchQuality(ScoredApplication left, ScoredApplication right)
    {
        var comparison = left.Score.CompareTo(right.Score);
        if (comparison != 0) return comparison;
        comparison = StringComparer.OrdinalIgnoreCase.Compare(right.Entry.DisplayName, left.Entry.DisplayName);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(right.Entry.DisplayName, left.Entry.DisplayName);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(right.Entry.Id, left.Entry.Id);
    }

    private static void SiftUpWorstFirst(ScoredApplication[] heap, int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (CompareMatchQuality(heap[index], heap[parent]) >= 0) return;
            (heap[parent], heap[index]) = (heap[index], heap[parent]);
            index = parent;
        }
    }

    private static void SiftDownWorstFirst(ScoredApplication[] heap, int count, int index)
    {
        while (true)
        {
            var left = index * 2 + 1;
            if (left >= count) return;
            var worst = left;
            var right = left + 1;
            if (right < count && CompareMatchQuality(heap[right], heap[left]) < 0) worst = right;
            if (CompareMatchQuality(heap[worst], heap[index]) >= 0) return;
            (heap[index], heap[worst]) = (heap[worst], heap[index]);
            index = worst;
        }
    }

    private static string SafeMessage(string value) =>
        new(value.Take(512).Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private static void Log(string eventName, string message)
    {
        if (ConsoleLog.IsEnabled) ConsoleLog.WriteLine($"[Index][{eventName}] {message}");
    }

    private static void Log(
        string eventName,
        ref ConsoleLogInterpolatedStringHandler message)
    {
        if (ConsoleLog.IsEnabled)
            ConsoleLog.WriteLine($"[Index][{eventName}] {message.GetFormattedText()}");
    }

    private sealed class ApplicationPartition(
        ApplicationPartitionDefinition definition,
        long ownershipEpoch,
        string scopeFingerprint,
        long nextPeriodicAt)
    {
        internal string SourceId { get; } = definition.SourceId;
        internal string? PortableRoot { get; } = definition.PortableRoot;
        internal string? ScopeRoot { get; } = definition.ScopeRoot;
        internal string? WatchRoot { get; } = definition.WatchRoot;
        internal string Category { get; } = definition.SourceId.StartsWith("portable:", StringComparison.Ordinal)
            ? "portable" : definition.SourceId.StartsWith("app-paths:", StringComparison.Ordinal)
                ? "registry" : "shell";
        internal long OwnershipEpoch { get; } = ownershipEpoch;
        internal string ScopeFingerprint { get; } = scopeFingerprint;
        internal Dictionary<string, long> TriggerTimes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal ApplicationEntry[] Entries { get; set; } = [];
        internal ApplicationPartitionCacheGeneration? TrustedCache { get; set; }
        internal long Generation { get; set; }
        internal long DirtyRevision { get; set; } = 1;
        internal long NextAllowedAt { get; set; }
        internal long NextPeriodicAt { get; set; } = nextPeriodicAt;
        internal string PendingCause { get; set; } = "startup";
        internal string Availability { get; set; } = "unknown";
        internal string Freshness { get; set; } = "unavailable";
        internal string State { get; set; } = "loading-cache";
        internal bool CacheLoaded { get; set; }
        internal bool HasUsableSnapshot { get; set; }
        internal bool Dirty { get; set; } = true;
        internal bool ForceRequested { get; set; }
        internal bool Running { get; set; }
        internal int FailureCount { get; set; }
    }

    private sealed record ApplicationPartitionDefinition(
        string SourceId,
        string? PortableRoot,
        string? ScopeRoot,
        string? WatchRoot);

    private sealed record RefreshTicket(
        string PartitionId,
        string? PortableRoot,
        long OwnershipEpoch,
        string Cause);

    private sealed record WatcherRegistration(
        string PartitionId,
        long OwnershipEpoch,
        string Root,
        FileSystemWatcher Watcher);

    private sealed record PublishedSource(string SourceId, ApplicationEntry[] Entries);
    private sealed record PendingPublication(long Revision, PublishedSource[] Sources);
    private readonly record struct PublishedSearchEntry(ApplicationEntry Entry, ApplicationNameIndex NameIndex);
    private readonly record struct ScoredApplication(ApplicationEntry Entry, int Score);

    private sealed record PublishedCatalog(PublishedSearchEntry[] SearchEntries, Dictionary<string, ApplicationEntry> ById)
    {
        internal static readonly PublishedCatalog Empty = new([], new(StringComparer.Ordinal));
    }

    private sealed class ApplicationEntryIdComparer : IComparer<ApplicationEntry>
    {
        internal static readonly ApplicationEntryIdComparer Instance = new();
        public int Compare(ApplicationEntry? left, ApplicationEntry? right) =>
            StringComparer.Ordinal.Compare(left?.Id, right?.Id);
    }
}
