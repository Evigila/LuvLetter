using LuvLetter.Core.Commands;
using LuvLetter.Core.Modules.QuickActions;

namespace LuvLetter.Core.Plugins;

/// <summary>
/// Collects one plugin's startup registrations without mutating the live registries.
/// </summary>
public sealed class PluginRegistrationContext
{
    private readonly List<PendingCommand> commands = [];
    private readonly List<PendingQuickAction> quickActions = [];
    private bool completed;

    internal PluginRegistrationContext() { }

    public void RegisterCommand(
        string commandName,
        Action<CommandInvocation> handler,
        CommandRegistrationMode mode = CommandRegistrationMode.RejectDuplicate)
    {
        ThrowIfCompleted();
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateMode(mode);

        var normalizedName = commandName.Trim();
        if (normalizedName.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "A command name cannot contain whitespace.",
                nameof(commandName));
        }

        var existingIndex = commands.FindIndex(
            registration => string.Equals(
                registration.Name,
                normalizedName,
                StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (mode == CommandRegistrationMode.RejectDuplicate)
            {
                throw new InvalidOperationException(
                    $"Command '{normalizedName}' is already registered by this plugin.");
            }

            commands[existingIndex] = new(normalizedName, handler, mode);
            return;
        }

        commands.Add(new(normalizedName, handler, mode));
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
        return new(commands.ToArray(), quickActions.ToArray());
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

    internal readonly record struct PendingCommand(
        string Name,
        Action<CommandInvocation> Handler,
        CommandRegistrationMode Mode);

    internal readonly record struct PendingQuickAction(
        QuickActionDefinition Definition,
        QuickActionRegistrationMode Mode);

    internal readonly record struct Batch(
        IReadOnlyList<PendingCommand> Commands,
        IReadOnlyList<PendingQuickAction> QuickActions);
}
