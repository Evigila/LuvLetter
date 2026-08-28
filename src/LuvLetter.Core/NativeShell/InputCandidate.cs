namespace LuvLetter.Core.NativeShell;

public enum CandidateKind
{
    File = 1,
    Command = 2,
    GlobalSearch = 3,
}

public enum CandidateIconKind
{
    None = 0,
    GenericFile = 1,
    Folder = 2,
    Image = 3,
    Document = 4,
    Archive = 5,
    Audio = 6,
    Video = 7,
    Executable = 8,
    Command = 9,
    Search = 10,
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
    CandidateIconKind IconKind,
    string PrimaryText,
    string SecondaryText);
