using LuvLetter.Core.NativeShell;

namespace LuvLetter.Core.Application;

internal static class CandidateIconClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".heif", ".ico", ".jpeg", ".jpg",
        ".png", ".svg", ".tif", ".tiff", ".webp",
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".doc", ".docx", ".epub", ".htm", ".html", ".json", ".log",
        ".md", ".odf", ".ods", ".odt", ".pdf", ".ppt", ".pptx", ".rtf",
        ".tex", ".toml", ".tsv", ".txt", ".xls", ".xlsx", ".xml", ".yaml", ".yml",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".bz2", ".cab", ".gz", ".iso", ".rar", ".tar", ".tgz", ".xz", ".zip",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".flac", ".m4a", ".mid", ".midi", ".mp3", ".ogg", ".opus", ".wav", ".wma",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".flv", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".webm", ".wmv",
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".com", ".exe", ".lnk", ".msi", ".ps1",
    };

    internal static CandidateIconKind Classify(FileSystemEntryKind entryKind, string fullPath)
    {
        if (entryKind == FileSystemEntryKind.Directory)
        {
            return CandidateIconKind.Folder;
        }

        if (entryKind != FileSystemEntryKind.File)
        {
            return CandidateIconKind.None;
        }

        var extension = Path.GetExtension(fullPath);
        if (ImageExtensions.Contains(extension)) return CandidateIconKind.Image;
        if (DocumentExtensions.Contains(extension)) return CandidateIconKind.Document;
        if (ArchiveExtensions.Contains(extension)) return CandidateIconKind.Archive;
        if (AudioExtensions.Contains(extension)) return CandidateIconKind.Audio;
        if (VideoExtensions.Contains(extension)) return CandidateIconKind.Video;
        if (ExecutableExtensions.Contains(extension)) return CandidateIconKind.Executable;
        return CandidateIconKind.GenericFile;
    }
}
