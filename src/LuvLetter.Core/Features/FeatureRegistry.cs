namespace LuvLetter.Core.Features;

public sealed class FeatureRegistry : IFeatureRegistrar
{
    private readonly object syncRoot = new();
    private List<FeatureDefinition> features = [];
    private Dictionary<string, int> indices = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    /// <summary>
    /// Registers a feature. Duplicate identifiers are rejected by default. Replacing a
    /// feature keeps its original position in the registration order.
    /// </summary>
    public bool Register(
        FeatureDefinition feature,
        FeatureRegistrationMode mode = FeatureRegistrationMode.RejectDuplicate)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ValidateRegistrationMode(mode);

        lock (syncRoot)
        {
            if (indices.TryGetValue(feature.Id, out var index))
            {
                if (mode == FeatureRegistrationMode.RejectDuplicate)
                {
                    return false;
                }

                features[index] = feature;
            }
            else
            {
                indices.Add(feature.Id, features.Count);
                features.Add(feature);
            }
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Registers a batch under one registry lock and publishes at most one change event.
    /// RejectDuplicate leaves the registry unchanged if an identifier already exists or
    /// occurs more than once in the batch. ReplaceExisting keeps an existing feature's
    /// position; within the batch, the last definition for an identifier wins while its
    /// first occurrence determines the position of a newly registered feature.
    /// </summary>
    public bool RegisterRange(
        IEnumerable<FeatureDefinition> featureDefinitions,
        FeatureRegistrationMode mode = FeatureRegistrationMode.RejectDuplicate)
    {
        ArgumentNullException.ThrowIfNull(featureDefinitions);
        ValidateRegistrationMode(mode);

        var batch = new List<FeatureDefinition>();
        var batchIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var hasDuplicate = false;

        foreach (var feature in featureDefinitions)
        {
            if (feature is null)
            {
                throw new ArgumentException(
                    "A feature registration batch cannot contain null.",
                    nameof(featureDefinitions));
            }

            if (batchIndices.TryGetValue(feature.Id, out var batchIndex))
            {
                if (mode == FeatureRegistrationMode.RejectDuplicate)
                {
                    hasDuplicate = true;
                }
                else
                {
                    batch[batchIndex] = feature;
                }

                continue;
            }

            batchIndices.Add(feature.Id, batch.Count);
            batch.Add(feature);
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
            if (mode == FeatureRegistrationMode.RejectDuplicate)
            {
                foreach (var feature in batch)
                {
                    if (indices.ContainsKey(feature.Id))
                    {
                        return false;
                    }
                }
            }

            // Build the complete next state before publishing it so enumeration,
            // validation, or allocation failures cannot partially register a batch.
            var nextFeatures = new List<FeatureDefinition>(features);
            var nextIndices = new Dictionary<string, int>(indices, StringComparer.Ordinal);
            foreach (var feature in batch)
            {
                if (nextIndices.TryGetValue(feature.Id, out var index))
                {
                    nextFeatures[index] = feature;
                }
                else
                {
                    nextIndices.Add(feature.Id, nextFeatures.Count);
                    nextFeatures.Add(feature);
                }
            }

            features = nextFeatures;
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

            features.RemoveAt(index);
            for (var current = index; current < features.Count; current++)
            {
                indices[features[current].Id] = current;
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

    public IReadOnlyList<FeatureDefinition> Snapshot()
    {
        lock (syncRoot)
        {
            return features.ToArray();
        }
    }

    public IReadOnlyList<FeatureItemSnapshot> ItemSnapshot()
    {
        lock (syncRoot)
        {
            var snapshot = new FeatureItemSnapshot[features.Count];
            for (var index = 0; index < features.Count; index++)
            {
                var feature = features[index];
                snapshot[index] = new FeatureItemSnapshot(feature.Id, feature.DisplayName);
            }

            return snapshot;
        }
    }

    public async ValueTask<FeatureActivationResult> ActivateAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        FeatureDefinition feature;
        lock (syncRoot)
        {
            if (!indices.TryGetValue(id.Trim(), out var index))
            {
                return new(FeatureActivationStatus.NotFound);
            }

            feature = features[index];
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await feature.ActivateAsync(cancellationToken).ConfigureAwait(false);
            return new(FeatureActivationStatus.Succeeded);
        }
        catch (OperationCanceledException exception)
        {
            return new(FeatureActivationStatus.Canceled, exception);
        }
        catch (Exception exception)
        {
            return new(FeatureActivationStatus.Failed, exception);
        }
    }

    private static void ValidateRegistrationMode(FeatureRegistrationMode mode)
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
