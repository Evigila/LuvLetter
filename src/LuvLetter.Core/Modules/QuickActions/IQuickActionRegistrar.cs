namespace LuvLetter.Core.Modules.QuickActions;

/// <summary>
/// Minimal quick-action registration capability exposed to plugin infrastructure.
/// </summary>
public interface IQuickActionRegistrar
{
    bool Register(
        QuickActionDefinition quickAction,
        QuickActionRegistrationMode mode = QuickActionRegistrationMode.RejectDuplicate);

    bool IsRegistered(string id);
}
