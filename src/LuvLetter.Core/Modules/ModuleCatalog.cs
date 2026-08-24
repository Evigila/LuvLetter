using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace LuvLetter.Core.Modules;

public static class ModuleCatalog
{
    public static ModuleDiscoveryResult Discover(
        IEnumerable<ILuvLetterModule> builtInModules,
        string? modulesDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(builtInModules);

        var modules = new List<ILuvLetterModule>();
        var warnings = new List<string>();
        var moduleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in builtInModules)
        {
            AddModule(module, "built-in modules", modules, moduleIds, warnings);
        }

        modulesDirectory ??= Path.Combine(AppContext.BaseDirectory, "modules");
        if (!Directory.Exists(modulesDirectory))
        {
            return new(modules, warnings);
        }

        foreach (var assemblyPath in Directory.EnumerateFiles(
                     modulesDirectory,
                     "*.dll",
                     SearchOption.TopDirectoryOnly)
                 .Order(StringComparer.OrdinalIgnoreCase))
        {
            DiscoverAssemblyModules(assemblyPath, modules, moduleIds, warnings);
        }

        return new(modules, warnings);
    }

    private static void DiscoverAssemblyModules(
        string assemblyPath,
        List<ILuvLetterModule> modules,
        HashSet<string> moduleIds,
        List<string> warnings)
    {
        try
        {
            var loadContext = new ModuleLoadContext(assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            foreach (var type in GetLoadableTypes(assembly, warnings)
                         .Where(static type =>
                             typeof(ILuvLetterModule).IsAssignableFrom(type)
                             && type is { IsAbstract: false, IsInterface: false })
                         .OrderBy(static type => type.FullName, StringComparer.Ordinal))
            {
                try
                {
                    if (Activator.CreateInstance(type) is not ILuvLetterModule module)
                    {
                        warnings.Add(
                            $"Module type '{type.FullName}' needs a public parameterless constructor.");
                        continue;
                    }

                    AddModule(module, assemblyPath, modules, moduleIds, warnings);
                }
                catch (Exception exception)
                {
                    warnings.Add(
                        $"Cannot create module '{type.FullName}': {exception.GetBaseException().Message}");
                }
            }
        }
        catch (Exception exception)
        {
            warnings.Add(
                $"Cannot load module assembly '{Path.GetFileName(assemblyPath)}': "
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

    private static void AddModule(
        ILuvLetterModule? module,
        string source,
        List<ILuvLetterModule> modules,
        HashSet<string> moduleIds,
        List<string> warnings)
    {
        if (module is null)
        {
            warnings.Add($"A null module was ignored from {source}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(module.Id))
        {
            warnings.Add($"Module '{module.GetType().FullName}' has no identifier and was ignored.");
            return;
        }

        var moduleId = module.Id.Trim();
        if (!moduleIds.Add(moduleId))
        {
            warnings.Add($"Duplicate module identifier '{moduleId}' was ignored.");
            return;
        }

        modules.Add(module);
    }

    private sealed class ModuleLoadContext(string assemblyPath)
        : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(
                    assemblyName.Name,
                    typeof(ILuvLetterModule).Assembly.GetName().Name,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var dependencyPath = resolver.ResolveAssemblyToPath(assemblyName);
            return dependencyPath is null ? null : LoadFromAssemblyPath(dependencyPath);
        }
    }
}
