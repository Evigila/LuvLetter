using System.Text.Json;
using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

public sealed class LuvLetterConfigurationStore
{
    private readonly string settingsPath;

    public LuvLetterConfigurationStore()
        : this(CreateDefaultSettingsPath())
    {
    }

    public LuvLetterConfigurationStore(string settingsPath)
    {
        this.settingsPath = settingsPath;
        Current = Normalize(Load());
    }

    public LuvLetterConfiguration Current { get; private set; }

    public event EventHandler<LuvLetterConfiguration>? Changed;

    public void Update(LuvLetterConfiguration configuration)
    {
        Current = Normalize(configuration);
        Save(Current);
        Changed?.Invoke(this, Current);
    }

    private LuvLetterConfiguration Load()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return LuvLetterConfiguration.Default;
            }

            var json = File.ReadAllText(settingsPath);
            if (JsonSerializer.Deserialize<LuvLetterConfiguration>(json) is { } configuration)
            {
                return configuration;
            }

            return LuvLetterConfiguration.Default;
        }
        catch
        {
            return TryLoadLegacyHotkey();
        }
    }

    private LuvLetterConfiguration TryLoadLegacyHotkey()
    {
        try
        {
            var json = File.ReadAllText(settingsPath);
            var legacyHotkey = JsonSerializer.Deserialize<HotkeyDefinition>(json);
            if (legacyHotkey is null)
            {
                return LuvLetterConfiguration.Default;
            }

            return LuvLetterConfiguration.Default with
            {
                InputBox = LuvLetterConfiguration.Default.InputBox with
                {
                    Hotkeys = LuvLetterConfiguration.Default.InputBox.Hotkeys with
                    {
                        Activation = legacyHotkey,
                    },
                },
            };
        }
        catch
        {
            return LuvLetterConfiguration.Default;
        }
    }

    private void Save(LuvLetterConfiguration configuration)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(
            configuration,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json);
    }

    private static string CreateDefaultSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LuvLetter", "settings.json");
    }

    private static LuvLetterConfiguration Normalize(LuvLetterConfiguration configuration)
    {
        var hotkeys = configuration.InputBox.Hotkeys;
        if (hotkeys.Activation.Modifiers == HotkeyModifierKeys.None)
        {
            hotkeys = hotkeys with { Activation = HotkeyDefinition.Default };
        }

        var placement = configuration.InputBox.Placement;
        if (placement is
            {
                Mode: InputBoxPositionMode.CenterBottom,
                OffsetX: 0,
                OffsetY: 0,
                BottomMargin: 120,
                CustomX: 0,
                CustomY: 0,
            })
        {
            placement = placement with
            {
                BottomMargin = LuvLetterConfiguration.Default.InputBox.Placement.BottomMargin,
            };
        }

        return configuration with
        {
            InputBox = configuration.InputBox with
            {
                Hotkeys = hotkeys,
                Placement = placement,
            },
        };
    }
}
