using LuvLetter.Core.Application;
using LuvLetter.Core.Plugins;

namespace LuvLetter.Core.Modules.Settings;

public sealed class SettingsPlugin(IApplicationShell applicationShell) : ILuvLetterPlugin
{
    public string Id => "core.settings";

    public void Register(PluginRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterCommand("luv", "settings", _ => applicationShell.ShowSettings());
        context.RegisterQuickAction(
            "settings.open",
            "Control Center",
            applicationShell.ShowSettings);
    }
}
