namespace LuvLetter.Core.Commands;

/// <summary>
/// Minimal command registration capability exposed to module infrastructure.
/// </summary>
public interface ICommandRegistrar
{
    bool Register(
        string commandName,
        Action<CommandInvocation> handler,
        CommandRegistrationMode mode = CommandRegistrationMode.RejectDuplicate);

    bool IsRegistered(string commandName);
}
