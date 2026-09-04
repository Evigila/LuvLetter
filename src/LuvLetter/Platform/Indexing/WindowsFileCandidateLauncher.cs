using System.IO;
using LuvLetter.Core.Application;
using LuvLetter.Platform.Applications;

namespace LuvLetter.Platform.Indexing;

internal sealed class WindowsFileCandidateLauncher : IFileCandidateLauncher
{
    public bool Open(string fullPath, FileSystemEntryKind entryKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!Exists(fullPath, entryKind))
        {
            return false;
        }

        return RequireSuccess(WindowsShell.ActivateAsync(
            () => WindowsShell.Execute(fullPath), CancellationToken.None).GetAwaiter().GetResult());
    }

    public bool Reveal(string fullPath, FileSystemEntryKind entryKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!Exists(fullPath, entryKind))
        {
            return false;
        }

        if (entryKind == FileSystemEntryKind.Directory
            && Directory.GetParent(Path.GetFullPath(fullPath)) is null)
        {
            return Open(fullPath, entryKind);
        }

        return RequireSuccess(WindowsShell.ActivateAsync(
            () => WindowsShell.Reveal(fullPath), CancellationToken.None).GetAwaiter().GetResult());
    }

    private static bool RequireSuccess(ApplicationLaunchResult result) => result.Succeeded
        ? true : throw new InvalidOperationException(result.Message);

    private static bool Exists(string fullPath, FileSystemEntryKind entryKind) => entryKind switch
    {
        FileSystemEntryKind.File => File.Exists(fullPath),
        FileSystemEntryKind.Directory => Directory.Exists(fullPath),
        _ => false,
    };
}
