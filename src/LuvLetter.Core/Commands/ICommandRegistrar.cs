using ArkheideSystem;

namespace LuvLetter.Core.Commands;

/// <summary>
/// Minimal command registration capability exposed to module infrastructure.
/// </summary>
public interface ICommandRegistrar
{
    bool Register(
        string commandDomain,
        string commandPath,
        Action<CommandInvocation> handler,
        CommandRegistrationMode mode = CommandRegistrationMode.RejectDuplicate,
        IReadOnlyList<CommandOption>? options = null);

    bool RegisterAlias(
        string aliasDomain,
        string aliasPath,
        string targetDomain,
        string targetPath);

    bool RegisterLink(
        string sourceDomain,
        string sourcePath,
        string targetDomain,
        string targetPath);

    bool IsRegistered(string commandDomain, string commandPath);

    bool IsExecutable(string commandDomain, string commandPath);

    bool HasPath(string commandDomain, string commandPath);
}
