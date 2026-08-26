namespace LuvLetter.Core.Plugins;

/// <summary>
/// Describes the active plugin set and owns the lifetime of successfully loaded plugins.
/// Plugin registrations are startup state and remain in their registries until those
/// registries are disposed with the application.
/// </summary>
public sealed class PluginSession : IDisposable
{
    private readonly IReadOnlyList<IDisposable> pluginLifetimes;
    private int disposed;

    internal PluginSession(
        IReadOnlyList<string> registeredPluginIds,
        IReadOnlyList<string> warnings,
        IReadOnlyList<IDisposable> pluginLifetimes)
    {
        RegisteredPluginIds = registeredPluginIds;
        Warnings = warnings;
        this.pluginLifetimes = pluginLifetimes;
    }

    public IReadOnlyList<string> RegisteredPluginIds { get; }

    public IReadOnlyList<string> Warnings { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        for (var index = pluginLifetimes.Count - 1; index >= 0; index--)
        {
            try
            {
                pluginLifetimes[index].Dispose();
            }
            catch
            {
                // One plugin cannot prevent the remaining plugins from shutting down.
            }
        }
    }
}
