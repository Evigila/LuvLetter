namespace LuvLetter.Core.Features;

public sealed class FeatureRegistry
{
    private readonly object syncRoot = new();
    private readonly List<FeatureDefinition> features = [];
    private readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);

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
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

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

    public IReadOnlyList<FeatureDefinition> Snapshot()
    {
        lock (syncRoot)
        {
            return features.ToArray();
        }
    }

    public bool TryActivate(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        FeatureDefinition feature;
        lock (syncRoot)
        {
            if (!indices.TryGetValue(id.Trim(), out var index))
            {
                return false;
            }

            feature = features[index];
        }

        return TryActivate(feature);
    }

    /// <summary>Activates the feature at a zero-based snapshot index.</summary>
    public bool TryActivate(int index)
    {
        FeatureDefinition feature;
        lock (syncRoot)
        {
            if ((uint)index >= (uint)features.Count)
            {
                return false;
            }

            feature = features[index];
        }

        return TryActivate(feature);
    }

    private static bool TryActivate(FeatureDefinition feature)
    {
        try
        {
            feature.Activate();
            return true;
        }
        catch
        {
            return false;
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
