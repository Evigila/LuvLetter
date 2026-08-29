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

public enum FileIndexRuntimeActivity
{
    Unavailable,
    Ready,
    InitialBuild,
    Updating,
    Failed,
}

public sealed record FileIndexRuntimeState(
    FileIndexRuntimeActivity Activity,
    ulong Generation)
{
    public static FileIndexRuntimeState Unavailable { get; } = new(
        FileIndexRuntimeActivity.Unavailable,
        0);
}

public interface IFileIndexClient
{
    event Action? IndexChanged;

    event Action<FileIndexRuntimeState>? StateChanged;

    FileIndexRuntimeState CurrentState { get; }

    ValueTask<IReadOnlyList<FileIndexMatch>> QueryAsync(
        string query,
        int maximumResults,
        ulong editorRevision,
        CancellationToken cancellationToken);
}

public interface IFileCandidateLauncher
{
    /// <returns>
    /// <see langword="false"/> only when the indexed target no longer exists;
    /// shell activation failures are reported by throwing.
    /// </returns>
    bool Open(string fullPath, FileSystemEntryKind entryKind);

    /// <inheritdoc cref="Open"/>
    bool Reveal(string fullPath, FileSystemEntryKind entryKind);
}
