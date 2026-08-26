namespace LuvLetter.Core.Modules.QuickActions;

public enum QuickActionRegistrationMode
{
    RejectDuplicate,
    ReplaceExisting,
}

public enum QuickActionActivationStatus
{
    Succeeded,
    NotFound,
    Canceled,
    Failed,
}

public sealed record QuickActionActivationResult(
    QuickActionActivationStatus Status,
    Exception? Exception = null)
{
    public bool Succeeded => Status == QuickActionActivationStatus.Succeeded;
}

public readonly record struct QuickActionSnapshot(string Id, string DisplayName);

public sealed class QuickActionDefinition
{
    public QuickActionDefinition(string id, string displayName, Action activate)
        : this(id, displayName, WrapSynchronousActivation(activate))
    {
    }

    public QuickActionDefinition(
        string id,
        string displayName,
        Func<CancellationToken, ValueTask> activateAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(activateAsync);

        Id = id.Trim();
        DisplayName = displayName.Trim();
        ActivateAsync = activateAsync;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public Func<CancellationToken, ValueTask> ActivateAsync { get; }

    private static Func<CancellationToken, ValueTask> WrapSynchronousActivation(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        return _ =>
        {
            activate();
            return ValueTask.CompletedTask;
        };
    }
}

public sealed class QuickActionRegistry : IQuickActionRegistrar
{
    private readonly object syncRoot = new();
    private List<QuickActionDefinition> quickActions = [];
    private Dictionary<string, int> indices = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    /// <summary>
    /// Registers a quick action. Duplicate identifiers are rejected by default. Replacing
    /// an action keeps its original position in the registration order.
    /// </summary>
    public bool Register(
        QuickActionDefinition quickAction,
        QuickActionRegistrationMode mode = QuickActionRegistrationMode.RejectDuplicate)
    {
        ArgumentNullException.ThrowIfNull(quickAction);
        ValidateRegistrationMode(mode);

        lock (syncRoot)
        {
            if (indices.TryGetValue(quickAction.Id, out var index))
            {
                if (mode == QuickActionRegistrationMode.RejectDuplicate)
                {
                    return false;
                }

                quickActions[index] = quickAction;
            }
            else
            {
                indices.Add(quickAction.Id, quickActions.Count);
                quickActions.Add(quickAction);
            }
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Registers a batch under one registry lock and publishes at most one change event.
    /// RejectDuplicate leaves the registry unchanged if an identifier already exists or
    /// occurs more than once in the batch. ReplaceExisting keeps an existing action's
    /// position; within the batch, the last definition for an identifier wins while its
    /// first occurrence determines the position of a newly registered quick action.
    /// </summary>
    public bool RegisterRange(
        IEnumerable<QuickActionDefinition> quickActionDefinitions,
        QuickActionRegistrationMode mode = QuickActionRegistrationMode.RejectDuplicate)
    {
        ArgumentNullException.ThrowIfNull(quickActionDefinitions);
        ValidateRegistrationMode(mode);

        var batch = new List<QuickActionDefinition>();
        var batchIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var hasDuplicate = false;

        foreach (var quickAction in quickActionDefinitions)
        {
            if (quickAction is null)
            {
                throw new ArgumentException(
                    "A quick-action registration batch cannot contain null.",
                    nameof(quickActionDefinitions));
            }

            if (batchIndices.TryGetValue(quickAction.Id, out var batchIndex))
            {
                if (mode == QuickActionRegistrationMode.RejectDuplicate)
                {
                    hasDuplicate = true;
                }
                else
                {
                    batch[batchIndex] = quickAction;
                }

                continue;
            }

            batchIndices.Add(quickAction.Id, batch.Count);
            batch.Add(quickAction);
        }

        if (hasDuplicate)
        {
            return false;
        }

        if (batch.Count == 0)
        {
            return true;
        }

        lock (syncRoot)
        {
            if (mode == QuickActionRegistrationMode.RejectDuplicate)
            {
                foreach (var quickAction in batch)
                {
                    if (indices.ContainsKey(quickAction.Id))
                    {
                        return false;
                    }
                }
            }

            // Build the complete next state before publishing it so enumeration,
            // validation, or allocation failures cannot partially register a batch.
            var nextQuickActions = new List<QuickActionDefinition>(quickActions);
            var nextIndices = new Dictionary<string, int>(indices, StringComparer.Ordinal);
            foreach (var quickAction in batch)
            {
                if (nextIndices.TryGetValue(quickAction.Id, out var index))
                {
                    nextQuickActions[index] = quickAction;
                }
                else
                {
                    nextIndices.Add(quickAction.Id, nextQuickActions.Count);
                    nextQuickActions.Add(quickAction);
                }
            }

            quickActions = nextQuickActions;
            indices = nextIndices;
        }

        RaiseChanged();
        return true;
    }

    public bool Unregister(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        lock (syncRoot)
        {
            if (!indices.Remove(id.Trim(), out var index))
            {
                return false;
            }

            quickActions.RemoveAt(index);
            for (var current = index; current < quickActions.Count; current++)
            {
                indices[quickActions[current].Id] = current;
            }
        }

        RaiseChanged();
        return true;
    }

    public bool IsRegistered(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (syncRoot)
        {
            return indices.ContainsKey(id.Trim());
        }
    }

    public IReadOnlyList<QuickActionDefinition> Snapshot()
    {
        lock (syncRoot)
        {
            return quickActions.ToArray();
        }
    }

    public IReadOnlyList<QuickActionSnapshot> ItemSnapshot()
    {
        lock (syncRoot)
        {
            var snapshot = new QuickActionSnapshot[quickActions.Count];
            for (var index = 0; index < quickActions.Count; index++)
            {
                var quickAction = quickActions[index];
                snapshot[index] = new QuickActionSnapshot(
                    quickAction.Id,
                    quickAction.DisplayName);
            }

            return snapshot;
        }
    }

    public async ValueTask<QuickActionActivationResult> ActivateAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        QuickActionDefinition quickAction;
        lock (syncRoot)
        {
            if (!indices.TryGetValue(id.Trim(), out var index))
            {
                return new(QuickActionActivationStatus.NotFound);
            }

            quickAction = quickActions[index];
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await quickAction.ActivateAsync(cancellationToken).ConfigureAwait(false);
            return new(QuickActionActivationStatus.Succeeded);
        }
        catch (OperationCanceledException exception)
        {
            return new(QuickActionActivationStatus.Canceled, exception);
        }
        catch (Exception exception)
        {
            return new(QuickActionActivationStatus.Failed, exception);
        }
    }

    private static void ValidateRegistrationMode(QuickActionRegistrationMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // Registry state must not depend on notification consumers.
            }
        }
    }
}
