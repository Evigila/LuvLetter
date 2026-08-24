using LuvLetter.Core.Commands;
using LuvLetter.Core.Features;

namespace LuvLetter.Core.Modules;

/// <summary>
/// Registers modules as isolated batches. A failing optional module is reported and
/// skipped without preventing the remaining modules from starting.
/// </summary>
public static class ModuleRegistrar
{
    public static ModuleRegistrationResult Register(
        IEnumerable<ILuvLetterModule> modules,
        ICommandRegistrar commandRegistrar,
        IFeatureRegistrar featureRegistrar,
        Action openSettings)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(commandRegistrar);
        ArgumentNullException.ThrowIfNull(featureRegistrar);
        ArgumentNullException.ThrowIfNull(openSettings);

        var registeredModuleIds = new List<string>();
        var warnings = new List<string>();
        var moduleLifetimes = new List<IDisposable>();
        var moduleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (module is null)
            {
                warnings.Add("A null module was ignored during registration.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(module.Id))
            {
                warnings.Add(
                    $"Module '{module.GetType().FullName}' has no identifier and was ignored during registration.");
                continue;
            }

            var moduleId = module.Id.Trim();
            if (!moduleIds.Add(moduleId))
            {
                warnings.Add($"Duplicate module identifier '{moduleId}' was ignored during registration.");
                continue;
            }

            try
            {
                var context = new ModuleRegistrationContext(
                    commandRegistrar,
                    featureRegistrar,
                    openSettings);
                module.Register(context);
                context.Commit();
                registeredModuleIds.Add(moduleId);
                if (module is IDisposable lifetime)
                {
                    moduleLifetimes.Add(lifetime);
                }
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Module '{moduleId}' could not be registered: "
                    + exception.GetBaseException().Message);
                if (module is IDisposable lifetime)
                {
                    try
                    {
                        lifetime.Dispose();
                    }
                    catch
                    {
                        // The registration failure remains the primary diagnostic.
                    }
                }
            }
        }

        return new(registeredModuleIds, warnings, moduleLifetimes);
    }
}
