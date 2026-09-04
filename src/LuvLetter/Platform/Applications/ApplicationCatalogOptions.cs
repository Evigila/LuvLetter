using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LuvLetter.Platform.Indexing;

namespace LuvLetter.Platform.Applications;

internal sealed class ApplicationCatalogOptions
{
    public string[] PortableRoots { get; init; } = [];

    internal static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LuvLetter", "Applications");

    internal string[] NormalizedRoots()
    {
        if (PortableRoots is null || PortableRoots.Length > 64)
            throw new InvalidDataException("PortableRoots must contain at most 64 absolute directory paths.");
        return PortableRoots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("PortableRoots cannot contain empty paths.");
        value = Environment.ExpandEnvironmentVariables(value);
        if (!Path.IsPathFullyQualified(value))
            throw new InvalidDataException("PortableRoots must contain absolute directory paths.");
        value = FileIndexMaintenanceOptions.NormalizeScopePath(value);
        if (value.Length > 32767 || value.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0
            || value.Any(char.IsControl))
            throw new InvalidDataException("PortableRoots must contain exact paths without wildcards.");
        var root = Path.GetPathRoot(value) ?? string.Empty;
        if (value[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component.Length > 255 || component.EndsWith('.') || component.EndsWith(' ')
                || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("PortableRoots contains an invalid path component.");
        return value;
    }

    internal static async Task<ApplicationCatalogOptions> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path))
        {
            var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var created = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(created, new ApplicationCatalogOptions(),
                        new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
                    await created.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // A concurrently created configuration belongs to its author.
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024)
            throw new InvalidDataException("Application settings exceed the 64 KiB limit.");
        var options = await JsonSerializer.DeserializeAsync<ApplicationCatalogOptions>(stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 8,
            }, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Application settings are empty.");
        _ = options.NormalizedRoots();
        return options;
    }
}
