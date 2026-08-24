using LuvLetter.Core.Commands;
using LuvLetter.Core.Features;

namespace LuvLetter.Core.Modules;

/// <summary>
/// Collects one module's registrations. The registrar commits the complete batch only
/// after the module returns successfully, so an exception cannot leave partial state.
/// </summary>
public sealed class ModuleRegistrationContext
{
    private readonly ICommandRegistrar commandRegistrar;
    private readonly IFeatureRegistrar featureRegistrar;
    private readonly Action openSettings;
    private readonly List<PendingCommand> commands = [];
    private readonly List<PendingFeature> features = [];
    private bool committed;

    internal ModuleRegistrationContext(
        ICommandRegistrar commandRegistrar,
        IFeatureRegistrar featureRegistrar,
        Action openSettings)
    {
        ArgumentNullException.ThrowIfNull(commandRegistrar);
        ArgumentNullException.ThrowIfNull(featureRegistrar);
        ArgumentNullException.ThrowIfNull(openSettings);
        this.commandRegistrar = commandRegistrar;
        this.featureRegistrar = featureRegistrar;
        this.openSettings = openSettings;
    }

    public void RegisterCommand(
        string commandName,
        Action<CommandInvocation> handler,
        CommandRegistrationMode mode = CommandRegistrationMode.RejectDuplicate)
    {
        ThrowIfCommitted();
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
                    $"Command '{normalizedName}' is already registered by this module.");
            }

            commands[existingIndex] = new(normalizedName, handler, mode);
            return;
        }

        commands.Add(new(normalizedName, handler, mode));
    }

    public void RegisterFeature(
        string id,
        string displayName,
        Action activate,
        FeatureRegistrationMode mode = FeatureRegistrationMode.RejectDuplicate) =>
        AddFeature(new FeatureDefinition(id, displayName, activate), mode);

    public void RegisterFeature(
        string id,
        string displayName,
        Func<CancellationToken, ValueTask> activateAsync,
        FeatureRegistrationMode mode = FeatureRegistrationMode.RejectDuplicate) =>
        AddFeature(new FeatureDefinition(id, displayName, activateAsync), mode);

    public void OpenSettings() => openSettings();

    internal void Commit()
    {
        ThrowIfCommitted();

        foreach (var command in commands)
        {
            if (command.Mode == CommandRegistrationMode.RejectDuplicate
                && commandRegistrar.IsRegistered(command.Name))
            {
                throw new InvalidOperationException(
                    $"Command '{command.Name}' is already registered.");
            }
        }

        foreach (var feature in features)
        {
            if (feature.Mode == FeatureRegistrationMode.RejectDuplicate
                && featureRegistrar.IsRegistered(feature.Definition.Id))
            {
                throw new InvalidOperationException(
                    $"Feature '{feature.Definition.Id}' is already registered.");
            }
        }

        foreach (var command in commands)
        {
            if (!commandRegistrar.Register(command.Name, command.Handler, command.Mode))
            {
                throw new InvalidOperationException(
                    $"Command '{command.Name}' could not be committed.");
            }
        }

        foreach (var feature in features)
        {
            if (!featureRegistrar.Register(feature.Definition, feature.Mode))
            {
                throw new InvalidOperationException(
                    $"Feature '{feature.Definition.Id}' could not be committed.");
            }
        }

        committed = true;
    }

    private void AddFeature(FeatureDefinition feature, FeatureRegistrationMode mode)
    {
        ThrowIfCommitted();
        ValidateMode(mode);

        var existingIndex = features.FindIndex(
            registration => string.Equals(
                registration.Definition.Id,
                feature.Id,
                StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            if (mode == FeatureRegistrationMode.RejectDuplicate)
            {
                throw new InvalidOperationException(
                    $"Feature '{feature.Id}' is already registered by this module.");
            }

            features[existingIndex] = new(feature, mode);
            return;
        }

        features.Add(new(feature, mode));
    }

    private void ThrowIfCommitted()
    {
        if (committed)
        {
            throw new InvalidOperationException("The module registration batch is already committed.");
        }
    }

    private static void ValidateMode(CommandRegistrationMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static void ValidateMode(FeatureRegistrationMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private sealed record PendingCommand(
        string Name,
        Action<CommandInvocation> Handler,
        CommandRegistrationMode Mode);

    private sealed record PendingFeature(
        FeatureDefinition Definition,
        FeatureRegistrationMode Mode);
}
