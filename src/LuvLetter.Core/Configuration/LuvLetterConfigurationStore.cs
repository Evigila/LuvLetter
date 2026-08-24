namespace LuvLetter.Core.Configuration;

/// <summary>
/// Owns the current configuration snapshot and coordinates persistence and change publication.
/// </summary>
public sealed class LuvLetterConfigurationStore : ILuvLetterConfigurationStore
{
    private readonly object syncRoot = new();
    private readonly JsonConfigurationRepository repository;
    private LuvLetterConfiguration current;

    public LuvLetterConfigurationStore()
        : this(CreateDefaultSettingsPath())
    {
    }

    public LuvLetterConfigurationStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        repository = new JsonConfigurationRepository(Path.GetFullPath(settingsPath));

        var loadResult = repository.Load();
        current = Normalize(loadResult.Configuration);
        InitialLoad = loadResult with { Configuration = current };
    }

    public ConfigurationLoadResult InitialLoad { get; }

    public LuvLetterConfiguration Current
    {
        get
        {
            lock (syncRoot)
            {
                return current;
            }
        }
    }

    public event EventHandler<LuvLetterConfiguration>? Changed;

    /// <summary>
    /// Validates and atomically persists a configuration, then publishes the exact
    /// normalized instance that became current. If persistence fails, Current is unchanged.
    /// </summary>
    public LuvLetterConfiguration Update(LuvLetterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var normalized = Normalize(configuration);

        lock (syncRoot)
        {
            repository.Save(normalized);
            current = normalized;
        }

        RaiseChanged(normalized);
        return normalized;
    }

    /// <summary>
    /// Returns a current-schema configuration whose values satisfy all runtime constraints.
    /// </summary>
    public static LuvLetterConfiguration Normalize(LuvLetterConfiguration? configuration) =>
        ConfigurationNormalizer.Normalize(configuration);

    private static string CreateDefaultSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LuvLetter", "settings.json");
    }

    private void RaiseChanged(LuvLetterConfiguration configuration)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<LuvLetterConfiguration> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, configuration);
            }
            catch
            {
                // A saved configuration remains current regardless of listeners.
            }
        }
    }
}
