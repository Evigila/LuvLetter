using System.Diagnostics;
using System.IO;
using LuvLetter.Core.Application;

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

        using var process = Process.Start(new ProcessStartInfo(fullPath)
        {
            UseShellExecute = true,
        });
        return process is not null;
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

        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
            Arguments = $"/select,\"{fullPath}\"",
        };
        using var process = Process.Start(startInfo);
        return process is not null;
    }

    private static bool Exists(string fullPath, FileSystemEntryKind entryKind) => entryKind switch
    {
        FileSystemEntryKind.File => File.Exists(fullPath),
        FileSystemEntryKind.Directory => Directory.Exists(fullPath),
        _ => false,
    };
}
