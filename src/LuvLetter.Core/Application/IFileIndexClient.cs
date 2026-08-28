namespace LuvLetter.Core.Application;

public enum FileSystemEntryKind
{
    File = 1,
    Directory = 2,
}

public sealed record FileIndexMatch(
    ulong StableId,
    FileSystemEntryKind EntryKind,
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
    bool Open(string fullPath, FileSystemEntryKind entryKind);

    bool Reveal(string fullPath, FileSystemEntryKind entryKind);
}
