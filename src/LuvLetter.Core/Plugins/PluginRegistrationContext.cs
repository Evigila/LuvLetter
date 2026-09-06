using LuvLetter.Core.Commands;
using LuvLetter.Core.Modules.QuickActions;
using ArkheideSystem;

namespace LuvLetter.Core.Plugins;

/// <summary>
/// Collects one plugin's startup registrations without mutating the live registries.
/// </summary>
public sealed class PluginRegistrationContext
{
    private readonly List<PendingCommand> commands = [];
    private readonly List<PendingCommandAlias> commandAliases = [];
    private readonly List<PendingCommandLink> commandLinks = [];
    private readonly List<PendingQuickAction> quickActions = [];
    private bool completed;

    internal PluginRegistrationContext() { }

    public void RegisterCommand(
        string commandDomain,
        string commandPath,
        Action<CommandInvocation> handler,
        CommandRegistrationMode mode = CommandRegistrationMode.RejectDuplicate,
        IReadOnlyList<CommandOption>? options = null)
    {
        ThrowIfCompleted();
        var normalizedDomain = CommandDispatcher.NormalizeDomain(
            commandDomain,
            nameof(commandDomain));
        var normalizedPath = CommandDispatcher.NormalizePath(commandPath, nameof(commandPath));
        ArgumentNullException.ThrowIfNull(handler);
        ValidateMode(mode);
        var registeredOptions = options?.ToArray() ?? [];
        if (registeredOptions.Any(static option => option is null))
        {
            throw new ArgumentException("Command options cannot contain null entries.", nameof(options));
        }
        var optionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (registeredOptions.SelectMany(static option => option.Names).Any(name => !optionNames.Add(name)))
        {
            throw new ArgumentException("Command option names must be unique.", nameof(options));
        }

        var existingIndex = commands.FindIndex(
            registration => string.Equals(
                registration.Domain,
                normalizedDomain,
                StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                registration.Path,
                normalizedPath,
                StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (mode == CommandRegistrationMode.RejectDuplicate)
            {
                throw new InvalidOperationException(
                    $"Command '{normalizedDomain} {normalizedPath}' is already registered by this plugin.");
            }

            commands[existingIndex] = new(
                normalizedDomain,
                normalizedPath,
                handler,
                mode,
                registeredOptions);
            return;
        }

        EnsureRouteAvailable(normalizedDomain, normalizedPath);
        commands.Add(new(normalizedDomain, normalizedPath, handler, mode, registeredOptions));
    }

    public void RegisterCommandAlias(
        string aliasDomain,
        string aliasPath,
        string targetDomain,
        string targetPath)
    {
        ThrowIfCompleted();
        var normalizedAliasDomain = CommandDispatcher.NormalizeDomain(
            aliasDomain,
            nameof(aliasDomain));
        var normalizedAliasPath = CommandDispatcher.NormalizePath(aliasPath, nameof(aliasPath));
        var normalizedTargetDomain = CommandDispatcher.NormalizeDomain(
            targetDomain,
            nameof(targetDomain));
        var normalizedTargetPath = CommandDispatcher.NormalizePath(targetPath, nameof(targetPath));
        EnsureRouteAvailable(normalizedAliasDomain, normalizedAliasPath);
        commandAliases.Add(new(
            normalizedAliasDomain,
            normalizedAliasPath,
            normalizedTargetDomain,
            normalizedTargetPath));
    }

    public void RegisterCommandLink(
        string sourceDomain,
        string sourcePath,
        string targetDomain,
        string targetPath)
    {
        ThrowIfCompleted();
        var normalizedSourceDomain = CommandDispatcher.NormalizeDomain(
            sourceDomain,
            nameof(sourceDomain));
        var normalizedSourcePath = CommandDispatcher.NormalizePath(sourcePath, nameof(sourcePath));
        var normalizedTargetDomain = CommandDispatcher.NormalizeDomain(
            targetDomain,
            nameof(targetDomain));
        var normalizedTargetPath = CommandDispatcher.NormalizePath(targetPath, nameof(targetPath));
        EnsureRouteAvailable(normalizedSourceDomain, normalizedSourcePath);
        commandLinks.Add(new(
            normalizedSourceDomain,
            normalizedSourcePath,
            normalizedTargetDomain,
            normalizedTargetPath));
    }

    public void RegisterQuickAction(
        string id,
        string displayName,
        Action activate,
        QuickActionRegistrationMode mode = QuickActionRegistrationMode.RejectDuplicate) =>
        AddQuickAction(new QuickActionDefinition(id, displayName, activate), mode);

    public void RegisterQuickAction(
        string id,
        string displayName,
        Func<CancellationToken, ValueTask> activateAsync,
        QuickActionRegistrationMode mode = QuickActionRegistrationMode.RejectDuplicate) =>
        AddQuickAction(new QuickActionDefinition(id, displayName, activateAsync), mode);

    internal Batch Complete()
    {
        ThrowIfCompleted();
        completed = true;
        return new(
            commands.ToArray(),
            commandAliases.ToArray(),
            commandLinks.ToArray(),
            quickActions.ToArray());
    }

    private void AddQuickAction(
        QuickActionDefinition quickAction,
        QuickActionRegistrationMode mode)
    {
        ThrowIfCompleted();
        ValidateMode(mode);

        var existingIndex = quickActions.FindIndex(
            registration => string.Equals(
                registration.Definition.Id,
                quickAction.Id,
                StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            if (mode == QuickActionRegistrationMode.RejectDuplicate)
            {
                throw new InvalidOperationException(
                    $"Quick action '{quickAction.Id}' is already registered by this plugin.");
            }

            quickActions[existingIndex] = new(quickAction, mode);
            return;
        }

        quickActions.Add(new(quickAction, mode));
    }

    private void ThrowIfCompleted()
    {
        if (completed)
        {
            throw new InvalidOperationException(
                "The plugin registration collection has already completed.");
        }
    }

    private static void ValidateMode(CommandRegistrationMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static void ValidateMode(QuickActionRegistrationMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private void EnsureRouteAvailable(string domain, string path)
    {
        if (commands.Any(command => SameRoute(command.Domain, command.Path, domain, path))
            || commandAliases.Any(alias => SameRoute(alias.Domain, alias.Path, domain, path))
            || commandLinks.Any(link => SameRoute(link.Domain, link.Path, domain, path)))
        {
            throw new InvalidOperationException(
                $"Command route '{domain} {path}' is already registered by this plugin.");
        }
    }

    private static bool SameRoute(
        string leftDomain,
        string leftPath,
        string rightDomain,
        string rightPath) =>
        string.Equals(leftDomain, rightDomain, StringComparison.OrdinalIgnoreCase)
        && string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);

    internal readonly record struct PendingCommand(
        string Domain,
        string Path,
        Action<CommandInvocation> Handler,
        CommandRegistrationMode Mode,
        IReadOnlyList<CommandOption> Options);

    internal readonly record struct PendingCommandAlias(
        string Domain,
        string Path,
        string TargetDomain,
        string TargetPath);

    internal readonly record struct PendingCommandLink(
        string Domain,
        string Path,
        string TargetDomain,
        string TargetPath);

    internal readonly record struct PendingQuickAction(
        QuickActionDefinition Definition,
        QuickActionRegistrationMode Mode);

    internal readonly record struct Batch(
        IReadOnlyList<PendingCommand> Commands,
        IReadOnlyList<PendingCommandAlias> CommandAliases,
        IReadOnlyList<PendingCommandLink> CommandLinks,
        IReadOnlyList<PendingQuickAction> QuickActions);
}
