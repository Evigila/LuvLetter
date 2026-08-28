using System.Diagnostics;
using LuvLetter.Core.Application;

namespace LuvLetter.Platform.Indexing;

internal sealed class WindowsFileCandidateLauncher : IFileCandidateLauncher
{
    public bool Open(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        using var process = Process.Start(new ProcessStartInfo(fullPath)
        {
            UseShellExecute = true,
        });
        return process is not null;
    }

    public bool Reveal(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
            Arguments = $"/select,\"{fullPath}\"",
        };
        using var process = Process.Start(startInfo);
        return process is not null;
    }
}
