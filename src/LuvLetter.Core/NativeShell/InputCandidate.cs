namespace LuvLetter.Core.NativeShell;

public enum CandidateKind
{
    File = 1,
    Command = 2,
    GlobalSearch = 3,
}

public enum CandidateAction
{
    Open = 0,
    Reveal = 1,
}

public sealed record InputChanged(
    string Text,
    InputMode Mode,
    ulong Revision);

public sealed record CandidateActivated(
    ulong Token,
    CandidateAction Action);

public sealed record InputCandidate(
    ulong Token,
    CandidateKind Kind,
    string PrimaryText,
    string SecondaryText);
