using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.QuickActions;

namespace LuvLetter.Core.NativeShell;

/// <summary>
/// Platform boundary used by the Core runtime to control the native Windows shell.
/// </summary>
public interface INativeShell
{
    event Action<InputSubmission>? InputSubmitted;

    event Action<string>? QuickActionActivated;

    event Action? QuickActionUnavailable;

    void ApplyConfiguration(
        InputBoxConfiguration inputBoxConfiguration,
        QuickActionsConfiguration quickActionsConfiguration);

    void SynchronizeQuickActions(IReadOnlyList<QuickActionSnapshot> quickActions);

    void ToggleCommandInput();

    void HideCommandInput();

    void ToggleQuickActions();

    void HideQuickActions();

    void EnqueueMessage(string message);

    void ToggleMessageQueue();

    void HideMessageQueue();

    void HidePopups();
}
