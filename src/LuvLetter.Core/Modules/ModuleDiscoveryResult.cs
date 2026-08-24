namespace LuvLetter.Core.Modules;

public sealed record ModuleDiscoveryResult(
    IReadOnlyList<ILuvLetterModule> Modules,
    IReadOnlyList<string> Warnings);
