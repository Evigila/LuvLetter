using LuvLetter.Core.Modules;

namespace LuvLetter.Modules;

internal sealed class BuiltInModule : ILuvLetterModule
{
    public string Id => "luvletter.builtin";

    public void Register(ModuleRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterCommand("settings", _ => context.OpenSettings());
        context.RegisterFeature("settings.open", "Open settings", context.OpenSettings);
    }
}
