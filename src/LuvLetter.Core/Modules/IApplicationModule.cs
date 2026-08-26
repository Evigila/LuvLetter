using LuvLetter.Core.Commands;
using LuvLetter.Core.Modules.QuickActions;

namespace LuvLetter.Core.Modules;

/// <summary>
/// A built-in LuvLetter capability. External dynamically loaded code uses Plugins instead.
/// </summary>
public interface IApplicationModule
{
    void Register(ICommandRegistrar commands, IQuickActionRegistrar quickActions);
}
