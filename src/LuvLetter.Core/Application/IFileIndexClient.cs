namespace LuvLetter.Core.Application;

public sealed record FileIndexMatch(
    ulong StableId,
    string DisplayName,
    string FullPath);

public interface IFileIndexClient
{
    event Action? IndexChanged;

    ValueTask<IReadOnlyList<FileIndexMatch>> QueryAsync(
        string query,
        int maximumResults,
        ulong editorRevision,
        CancellationToken cancellationToken);
}

public interface IFileCandidateLauncher
{
    bool Open(string fullPath);

    bool Reveal(string fullPath);
}
