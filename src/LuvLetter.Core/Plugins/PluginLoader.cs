using System.Reflection;
using System.Runtime.Loader;
using LuvLetter.Core.Commands;
using LuvLetter.Core.Modules.QuickActions;

namespace LuvLetter.Core.Plugins;

/// <summary>
/// Discovers optional plugins, collects their declarations, validates the complete startup
/// registration set, and only then mutates the live command and quick-action registries.
/// </summary>
public static class PluginLoader
{
    public static PluginSession Load(
        IEnumerable<ILuvLetterPlugin> builtInPlugins,
        ICommandRegistrar commandRegistrar,
        IQuickActionRegistrar quickActionRegistrar,
        string? pluginsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(builtInPlugins);
        ArgumentNullException.ThrowIfNull(commandRegistrar);
        ArgumentNullException.ThrowIfNull(quickActionRegistrar);

        var warnings = new List<string>();
        var disposedPlugins = new HashSet<ILuvLetterPlugin>(ReferenceEqualityComparer.Instance);
        var plugins = DiscoverPlugins(
            builtInPlugins,
            pluginsDirectory,
            warnings,
            disposedPlugins);
        var collectedPlugins = new List<CollectedPlugin>(plugins.Count);
        var plannedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedQuickActions = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var plugin in plugins)
            {
                PluginRegistrationContext.Batch batch;
                try
                {
                    var context = new PluginRegistrationContext();
                    plugin.Instance.Register(context);
                    batch = context.Complete();
                }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"Plugin '{plugin.Id}' could not collect registrations: "
                        + exception.GetBaseException().Message);
                    DisposePlugin(plugin.Instance, disposedPlugins);
                    continue;
                }

                if (!TryValidate(
                        batch,
                        commandRegistrar,
                        quickActionRegistrar,
                        plannedCommands,
                        plannedQuickActions,
                        out var validationError))
                {
                    warnings.Add($"Plugin '{plugin.Id}' was not loaded: {validationError}");
                    DisposePlugin(plugin.Instance, disposedPlugins);
                    continue;
                }

                Reserve(batch, plannedCommands, plannedQuickActions);
                collectedPlugins.Add(new(plugin.Id, plugin.Instance, batch));
            }

            Commit(collectedPlugins, commandRegistrar, quickActionRegistrar);
        }
        catch (Exception exception)
        {
            DisposeAll(plugins, disposedPlugins);
            throw new InvalidOperationException(
                "Plugin startup could not be completed. Registrations are prevalidated, "
                + "but the supplied registrars rejected or failed during commit; startup "
                + "must abort because registrar interfaces do not support rollback.",
                exception);
        }

        var registeredIds = collectedPlugins.Select(static plugin => plugin.Id).ToArray();
        var lifetimes = collectedPlugins
            .Select(static plugin => plugin.Instance)
            .OfType<IDisposable>()
            .ToArray();
        return new PluginSession(registeredIds, warnings.ToArray(), lifetimes);
    }

    private static List<DiscoveredPlugin> DiscoverPlugins(
        IEnumerable<ILuvLetterPlugin> builtInPlugins,
        string? pluginsDirectory,
        List<string> warnings,
        HashSet<ILuvLetterPlugin> disposedPlugins)
    {
        var plugins = new List<DiscoveredPlugin>();
        var pluginIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var plugin in builtInPlugins)
            {
                TryAddPlugin(
                    plugin,
                    "built-in plugins",
                    plugins,
                    pluginIds,
                    warnings,
                    disposedPlugins);
            }
        }
        catch (Exception exception)
        {
            DisposeAll(plugins, disposedPlugins);
            throw new InvalidOperationException(
                "Built-in plugins could not be enumerated.",
                exception);
        }

        pluginsDirectory ??= Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginsDirectory))
        {
            return plugins;
        }

        string[] assemblyPaths;
        try
        {
            assemblyPaths = Directory
                .EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(
                $"Cannot enumerate optional plugins in '{pluginsDirectory}': "
                + exception.Message);
            return plugins;
        }

        foreach (var assemblyPath in assemblyPaths)
        {
            DiscoverAssemblyPlugins(
                assemblyPath,
                plugins,
                pluginIds,
                warnings,
                disposedPlugins);
        }

        return plugins;
    }

    private static void DiscoverAssemblyPlugins(
        string assemblyPath,
        List<DiscoveredPlugin> plugins,
        HashSet<string> pluginIds,
        List<string> warnings,
        HashSet<ILuvLetterPlugin> disposedPlugins)
    {
        try
        {
            var loadContext = new PluginLoadContext(assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            foreach (var type in GetLoadableTypes(assembly, warnings)
                         .Where(static type =>
                             typeof(ILuvLetterPlugin).IsAssignableFrom(type)
                             && type is { IsAbstract: false, IsInterface: false })
                         .OrderBy(static type => type.FullName, StringComparer.Ordinal))
            {
                try
                {
                    if (Activator.CreateInstance(type) is not ILuvLetterPlugin plugin)
                    {
                        warnings.Add(
                            $"Plugin type '{type.FullName}' needs a public parameterless constructor.");
                        continue;
                    }

                    TryAddPlugin(
                        plugin,
                        assemblyPath,
                        plugins,
                        pluginIds,
                        warnings,
                        disposedPlugins);
                }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"Cannot create plugin '{type.FullName}': "
                        + exception.GetBaseException().Message);
                }
            }
        }
        catch (Exception exception)
        {
            warnings.Add(
                $"Cannot load plugin assembly '{Path.GetFileName(assemblyPath)}': "
                + exception.GetBaseException().Message);
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(
        Assembly assembly,
        List<string> warnings)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            foreach (var loaderException in exception.LoaderExceptions)
            {
                if (loaderException is not null)
                {
                    warnings.Add(
                        $"A type in '{assembly.GetName().Name}' could not be loaded: "
                        + loaderException.Message);
                }
            }

            return exception.Types.OfType<Type>();
        }
    }

    private static void TryAddPlugin(
        ILuvLetterPlugin? plugin,
        string source,
        List<DiscoveredPlugin> plugins,
        HashSet<string> pluginIds,
        List<string> warnings,
        HashSet<ILuvLetterPlugin> disposedPlugins)
    {
        if (plugin is null)
        {
            warnings.Add($"A null plugin was ignored from {source}.");
            return;
        }

        string pluginId;
        try
        {
            pluginId = plugin.Id?.Trim() ?? string.Empty;
        }
        catch (Exception exception)
        {
            warnings.Add(
                $"Cannot read the identifier of plugin '{plugin.GetType().FullName}': "
                + exception.GetBaseException().Message);
            DisposePlugin(plugin, disposedPlugins);
            return;
        }

        if (pluginId.Length == 0)
        {
            warnings.Add($"Plugin '{plugin.GetType().FullName}' has no identifier and was ignored.");
            DisposePlugin(plugin, disposedPlugins);
            return;
        }

        if (!pluginIds.Add(pluginId))
        {
            warnings.Add($"Duplicate plugin identifier '{pluginId}' was ignored.");
            DisposePlugin(plugin, disposedPlugins);
            return;
        }

        plugins.Add(new(pluginId, plugin));
    }

    private static bool TryValidate(
        PluginRegistrationContext.Batch batch,
        ICommandRegistrar commandRegistrar,
        IQuickActionRegistrar quickActionRegistrar,
        HashSet<string> plannedCommands,
        HashSet<string> plannedQuickActions,
        out string error)
    {
        foreach (var command in batch.Commands)
        {
            if (command.Mode == CommandRegistrationMode.RejectDuplicate
                && (plannedCommands.Contains(command.Name)
                    || commandRegistrar.IsRegistered(command.Name)))
            {
                error = $"command '{command.Name}' is already registered.";
                return false;
            }
        }

        foreach (var quickAction in batch.QuickActions)
        {
            if (quickAction.Mode == QuickActionRegistrationMode.RejectDuplicate
                && (plannedQuickActions.Contains(quickAction.Definition.Id)
                    || quickActionRegistrar.IsRegistered(quickAction.Definition.Id)))
            {
                error = $"quick action '{quickAction.Definition.Id}' is already registered.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static void Reserve(
        PluginRegistrationContext.Batch batch,
        HashSet<string> plannedCommands,
        HashSet<string> plannedQuickActions)
    {
        foreach (var command in batch.Commands)
        {
            plannedCommands.Add(command.Name);
        }

        foreach (var quickAction in batch.QuickActions)
        {
            plannedQuickActions.Add(quickAction.Definition.Id);
        }
    }

    private static void Commit(
        IReadOnlyList<CollectedPlugin> plugins,
        ICommandRegistrar commandRegistrar,
        IQuickActionRegistrar quickActionRegistrar)
    {
        foreach (var plugin in plugins)
        {
            foreach (var command in plugin.Batch.Commands)
            {
                if (!commandRegistrar.Register(command.Name, command.Handler, command.Mode))
                {
                    throw new InvalidOperationException(
                        $"Command '{command.Name}' from plugin '{plugin.Id}' was rejected during commit.");
                }
            }

            foreach (var quickAction in plugin.Batch.QuickActions)
            {
                if (!quickActionRegistrar.Register(quickAction.Definition, quickAction.Mode))
                {
                    throw new InvalidOperationException(
                        $"Quick action '{quickAction.Definition.Id}' from plugin '{plugin.Id}' "
                        + "was rejected during commit.");
                }
            }
        }
    }

    private static void DisposeAll(
        IReadOnlyList<DiscoveredPlugin> plugins,
        HashSet<ILuvLetterPlugin> disposedPlugins)
    {
        for (var index = plugins.Count - 1; index >= 0; index--)
        {
            DisposePlugin(plugins[index].Instance, disposedPlugins);
        }
    }

    private static void DisposePlugin(
        ILuvLetterPlugin plugin,
        HashSet<ILuvLetterPlugin> disposedPlugins)
    {
        if (plugin is not IDisposable lifetime || !disposedPlugins.Add(plugin))
        {
            return;
        }

        try
        {
            lifetime.Dispose();
        }
        catch
        {
            // Discovery/registration diagnostics remain the primary failure.
        }
    }

    private readonly record struct DiscoveredPlugin(string Id, ILuvLetterPlugin Instance);

    private readonly record struct CollectedPlugin(
        string Id,
        ILuvLetterPlugin Instance,
        PluginRegistrationContext.Batch Batch);

    private sealed class PluginLoadContext(string assemblyPath)
        : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(
                    assemblyName.Name,
                    typeof(ILuvLetterPlugin).Assembly.GetName().Name,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var dependencyPath = resolver.ResolveAssemblyToPath(assemblyName);
            return dependencyPath is null ? null : LoadFromAssemblyPath(dependencyPath);
        }
    }
}
