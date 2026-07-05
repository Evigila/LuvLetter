using System.Text.Json;

namespace LuvLetter.Core.Hotkeys;

public sealed class HotkeyConfigurationStore
{
    private readonly string settingsPath;

    public HotkeyConfigurationStore()
        : this(CreateDefaultSettingsPath())
    {
    }

    public HotkeyConfigurationStore(string settingsPath)
    {
        this.settingsPath = settingsPath;
        Current = Load();
    }

    public HotkeyDefinition Current { get; private set; }

    public event EventHandler<HotkeyDefinition>? Changed;

    public void Update(HotkeyDefinition hotkey)
    {
        Current = hotkey;
        Save(hotkey);
        Changed?.Invoke(this, hotkey);
    }

    private HotkeyDefinition Load()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return HotkeyDefinition.Default;
            }

            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<HotkeyDefinition>(json) ?? HotkeyDefinition.Default;
        }
        catch
        {
            return HotkeyDefinition.Default;
        }
    }

    private void Save(HotkeyDefinition hotkey)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(hotkey, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json);
    }

    private static string CreateDefaultSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LuvLetter", "settings.json");
    }
}
