using LuvLetter.Core.Commands;
using LuvLetter.Core.Modules.QuickActions;
using LuvLetter.Core.Application;

namespace LuvLetter.Core.Modules.Settings;

public sealed class SettingsModule(IApplicationShell applicationShell) : IApplicationModule
{
    public void Register(ICommandRegistrar commands, IQuickActionRegistrar quickActions)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(quickActions);

        if (!commands.Register("settings", _ => applicationShell.ShowSettings()))
        {
            throw new InvalidOperationException("The built-in settings command is duplicated.");
        }

        if (!quickActions.Register(
                new QuickActionDefinition(
                    "settings.open",
                    "Open settings",
                    applicationShell.ShowSettings)))
        {
            throw new InvalidOperationException(
                "The built-in settings quick action is duplicated.");
        }
    }
}
