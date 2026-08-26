using System.Text.Json;

namespace LuvLetter.Core.Configuration;

/// <summary>
/// Reads and atomically writes the JSON representation of the configuration.
/// </summary>
internal sealed class JsonConfigurationRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private readonly string settingsPath;

    internal JsonConfigurationRepository(string settingsPath)
    {
        this.settingsPath = settingsPath;
    }

    internal ConfigurationLoadResult Load()
    {
        try
        {
            using var stream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidConfiguration("The settings root must be a JSON object.");
            }

            if (ConfigurationSchemaMigrator.LooksLikeLegacyHotkey(document.RootElement))
            {
                return InvalidConfiguration(
                    "The legacy activation-hotkey format is no longer supported; default Ctrl gestures were restored.");
            }

            var serializedVersion = ConfigurationSchemaMigrator.ReadSchemaVersion(document.RootElement);
            if (serializedVersion > LuvLetterConfiguration.CurrentSchemaVersion)
            {
                return new(
                    LuvLetterConfiguration.Default,
                    ConfigurationLoadStatus.UnsupportedVersion,
                    $"Settings schema {serializedVersion} is newer than supported schema {LuvLetterConfiguration.CurrentSchemaVersion}.");
            }

            var migratedDocument = ConfigurationSchemaMigrator.MigrateDocument(
                document.RootElement);
            var configuration = migratedDocument.Deserialize<LuvLetterConfiguration>(
                SerializerOptions);
            return configuration is null
                ? InvalidConfiguration("The settings document did not contain a configuration.")
                : new(configuration, ConfigurationLoadStatus.Loaded);
        }
        catch (JsonException exception)
        {
            return InvalidConfiguration("The settings JSON is invalid.", exception);
        }
        catch (NotSupportedException exception)
        {
            return InvalidConfiguration("The settings document contains unsupported values.", exception);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(
                LuvLetterConfiguration.Default,
                ConfigurationLoadStatus.NotFound);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(
                LuvLetterConfiguration.Default,
                ConfigurationLoadStatus.IoFailure,
                $"The settings file could not be read: {exception.Message}",
                exception);
        }
    }

    internal void Save(LuvLetterConfiguration configuration)
    {
        var directory = Path.GetDirectoryName(settingsPath)
            ?? throw new InvalidOperationException("The settings path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                using var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions { Indented = true });
                JsonSerializer.Serialize(writer, configuration, SerializerOptions);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(settingsPath))
            {
                File.Replace(temporaryPath, settingsPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, settingsPath);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A failed cleanup must not hide the persistence result.
            }
        }
    }

    private static ConfigurationLoadResult InvalidConfiguration(
        string message,
        Exception? exception = null) =>
        new(
            LuvLetterConfiguration.Default,
            ConfigurationLoadStatus.Invalid,
            message,
            exception);
}
