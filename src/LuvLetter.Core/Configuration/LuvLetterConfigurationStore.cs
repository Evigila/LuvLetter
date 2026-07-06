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

        var colors = configuration.InputBox.Colors;
        if (string.Equals(colors.Text, "#F2191919", StringComparison.OrdinalIgnoreCase))
        {
            colors = colors with
            {
                Text = LuvLetterConfiguration.Default.InputBox.Colors.Text,
            };
        }

        if (string.Equals(colors.Caret, "#F2191919", StringComparison.OrdinalIgnoreCase))
        {
            colors = colors with
            {
                Caret = LuvLetterConfiguration.Default.InputBox.Colors.Caret,
            };
        }

        var backgroundOpacity = Math.Clamp(colors.BackgroundOpacity, 0.0f, 1.0f);
        if (
            string.Equals(colors.Background, "#66DCDCDC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(colors.Background, "#99DCDCDC", StringComparison.OrdinalIgnoreCase)
        )
        {
            backgroundOpacity = LuvLetterConfiguration.Default.InputBox.Colors.BackgroundOpacity;
            colors = colors with
            {
                Background = LuvLetterConfiguration.Default.InputBox.Colors.Background,
            };
        }

        colors = colors with
        {
            BackgroundOpacity = backgroundOpacity,
            Background = ApplyOpacityToColor(colors.Background, backgroundOpacity),
        };

        var size = configuration.InputBox.Size;
        if (
            size.Width == 640
            && size.Height == 56
            && IsNear(size.CornerRadius, 8.0f)
            && IsNear(size.BorderThickness, 2.0f)
            && IsNear(size.FontSize, 20.0f)
            && (IsNear(size.HorizontalPadding, 18.0f) || IsNear(size.HorizontalPadding, 12.0f))
        )
        {
            size = size with
            {
                Height = LuvLetterConfiguration.Default.InputBox.Size.Height,
                HorizontalPadding = LuvLetterConfiguration.Default.InputBox.Size.HorizontalPadding,
                VerticalPadding = LuvLetterConfiguration.Default.InputBox.Size.VerticalPadding,
                CaretWidth = LuvLetterConfiguration.Default.InputBox.Size.CaretWidth,
            };
        }

        return configuration with
        {
            InputBox = configuration.InputBox with
            {
                Hotkeys = hotkeys,
                Placement = placement,
                Colors = colors,
                Size = size,
            },
        };
    }

    private static bool IsNear(float value, float expected)
    {
        return Math.Abs(value - expected) < 0.001f;
    }

    private static string ApplyOpacityToColor(string value, float opacity)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 8)
        {
            hex = hex[^6..];
        }

        if (hex.Length != 6)
        {
            return LuvLetterConfiguration.Default.InputBox.Colors.Background;
        }

        var alpha = (int)Math.Round(Math.Clamp(opacity, 0.0f, 1.0f) * 255.0f);
        return $"#{alpha:X2}{hex}";
    }
}
