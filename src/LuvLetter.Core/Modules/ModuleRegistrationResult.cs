namespace LuvLetter.Core.Modules;

/// <summary>
/// Describes the active module set and owns the lifetime of successfully registered
/// modules that implement <see cref="IDisposable"/>.
/// </summary>
public sealed class ModuleRegistrationResult : IDisposable
{
    private readonly IReadOnlyList<IDisposable> moduleLifetimes;
    private int disposed;

    internal ModuleRegistrationResult(
        IReadOnlyList<string> registeredModuleIds,
        IReadOnlyList<string> warnings,
        IReadOnlyList<IDisposable> moduleLifetimes)
    {
        RegisteredModuleIds = registeredModuleIds;
        Warnings = warnings;
        this.moduleLifetimes = moduleLifetimes;
    }

    public IReadOnlyList<string> RegisteredModuleIds { get; }

    public IReadOnlyList<string> Warnings { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        for (var index = moduleLifetimes.Count - 1; index >= 0; index--)
        {
            try
            {
                moduleLifetimes[index].Dispose();
            }
            catch
            {
                // One module cannot prevent the remaining modules from shutting down.
            }
        }
    }
}
