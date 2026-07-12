using System.Globalization;
using System.Text.Json;
using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

public sealed class LuvLetterConfigurationStore
{
    private const HotkeyModifierKeys ValidModifiers =
        HotkeyModifierKeys.Alt
        | HotkeyModifierKeys.Control
        | HotkeyModifierKeys.Shift
        | HotkeyModifierKeys.Win;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private readonly object syncRoot = new();
    private readonly string settingsPath;
    private LuvLetterConfiguration current;

    public LuvLetterConfigurationStore()
        : this(CreateDefaultSettingsPath())
    {
    }

    public LuvLetterConfigurationStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = Path.GetFullPath(settingsPath);
        current = Normalize(Load());
    }

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
            Save(normalized);
            current = normalized;
        }

        RaiseChanged(normalized);
        return normalized;
    }

    public static LuvLetterConfiguration Normalize(LuvLetterConfiguration? configuration)
    {
        var defaults = LuvLetterConfiguration.Default;
        configuration ??= defaults;
        configuration = MigrateLegacyVisualDefaults(configuration, defaults);

        var inputBox = configuration.InputBox ?? defaults.InputBox;
        var hotkeys = inputBox.Hotkeys ?? defaults.InputBox.Hotkeys;
        var placement = NormalizePlacement(
            inputBox.Placement ?? defaults.InputBox.Placement,
            defaults.InputBox.Placement);
        var colors = NormalizeInputBoxColors(
            inputBox.Colors ?? defaults.InputBox.Colors,
            defaults.InputBox.Colors);
        var size = NormalizeInputBoxSize(
            inputBox.Size ?? defaults.InputBox.Size,
            defaults.InputBox.Size);

        var gestures = NormalizeActivationGestures(
            configuration.ActivationGestures ?? defaults.ActivationGestures,
            defaults.ActivationGestures);

        var featureWindow = configuration.FeatureWindow ?? defaults.FeatureWindow;
        var featureLayout = NormalizeFeatureLayout(
            featureWindow.Layout ?? defaults.FeatureWindow.Layout,
            defaults.FeatureWindow.Layout);
        var featureColors = NormalizeFeatureColors(
            featureWindow.Colors ?? defaults.FeatureWindow.Colors,
            defaults.FeatureWindow.Colors);
        var featureHotkeys = NormalizeFeatureHotkeys(
            featureWindow.Hotkeys ?? defaults.FeatureWindow.Hotkeys,
            defaults.FeatureWindow.Hotkeys,
            featureLayout.ItemsPerPage);

        return configuration with
        {
            SchemaVersion = LuvLetterConfiguration.CurrentSchemaVersion,
            InputBox = inputBox with
            {
                Hotkeys = hotkeys with
                {
                    Activation = NormalizeHotkey(
                        hotkeys.Activation,
                        defaults.InputBox.Hotkeys.Activation),
                    Submit = NormalizeHotkey(
                        hotkeys.Submit,
                        defaults.InputBox.Hotkeys.Submit),
                    Cancel = NormalizeHotkey(
                        hotkeys.Cancel,
                        defaults.InputBox.Hotkeys.Cancel),
                    Backspace = NormalizeHotkey(
                        hotkeys.Backspace,
                        defaults.InputBox.Hotkeys.Backspace),
                },
                Placement = placement,
                Colors = colors,
                Size = size,
            },
            ActivationGestures = gestures,
            FeatureWindow = featureWindow with
            {
                Layout = featureLayout,
                Colors = featureColors,
                Hotkeys = featureHotkeys,
            },
        };
    }

    private static LuvLetterConfiguration MigrateLegacyVisualDefaults(
        LuvLetterConfiguration configuration,
        LuvLetterConfiguration defaults)
    {
        if (configuration.SchemaVersion >= 3)
        {
            return configuration;
        }

        var migrated = configuration;
        var inputBox = configuration.InputBox;
        if (inputBox is not null
            && inputBox.Size is { } inputSize
            && inputBox.Colors is { } inputColors
            && NearlyEquals(inputSize.CornerRadius, 8.0f)
            && NearlyEquals(inputSize.BorderThickness, 2.0f)
            && IsLegacyOpaqueWhite(inputColors.Border))
        {
            migrated = migrated with
            {
                InputBox = inputBox with
                {
                    Size = inputSize with
                    {
                        CornerRadius = defaults.InputBox.Size.CornerRadius,
                        BorderThickness = defaults.InputBox.Size.BorderThickness,
                    },
                    Colors = inputColors with { Border = defaults.InputBox.Colors.Border },
                },
            };
        }

        var featureWindow = configuration.FeatureWindow;
        if (featureWindow is not null
            && featureWindow.Layout is { } featureLayout
            && featureWindow.Colors is { } featureColors
            && NearlyEquals(featureLayout.CornerRadius, 12.0f)
            && NearlyEquals(featureLayout.BorderThickness, 2.0f)
            && IsLegacyOpaqueWhite(featureColors.Border))
        {
            migrated = migrated with
            {
                FeatureWindow = featureWindow with
                {
                    Layout = featureLayout with
                    {
                        CornerRadius = defaults.FeatureWindow.Layout.CornerRadius,
                        BorderThickness = defaults.FeatureWindow.Layout.BorderThickness,
                    },
                    Colors = featureColors with { Border = defaults.FeatureWindow.Colors.Border },
                },
            };
        }

        return migrated;
    }

    private static bool NearlyEquals(float left, float right) =>
        Math.Abs(left - right) < 0.0001f;

    private static bool IsLegacyOpaqueWhite(string? color)
    {
        var normalized = color?.Trim().TrimStart('#');
        return string.Equals(normalized, "FFFFFFFF", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "FFFFFF", StringComparison.OrdinalIgnoreCase);
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
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return LuvLetterConfiguration.Default;
            }

            if (LooksLikeLegacyHotkey(document.RootElement))
            {
                return LoadLegacyHotkey(json);
            }

            return JsonSerializer.Deserialize<LuvLetterConfiguration>(json, SerializerOptions)
                ?? LuvLetterConfiguration.Default;
        }
        catch
        {
            return LuvLetterConfiguration.Default;
        }
    }

    private static bool LooksLikeLegacyHotkey(JsonElement root)
    {
        var hasVirtualKey = false;
        var hasInputBox = false;
        foreach (var property in root.EnumerateObject())
        {
            hasVirtualKey |= property.Name.Equals(
                nameof(HotkeyDefinition.VirtualKey),
                StringComparison.OrdinalIgnoreCase);
            hasInputBox |= property.Name.Equals(
                nameof(LuvLetterConfiguration.InputBox),
                StringComparison.OrdinalIgnoreCase);
        }

        return hasVirtualKey && !hasInputBox;
    }

    private static LuvLetterConfiguration LoadLegacyHotkey(string json)
    {
        var legacyHotkey = JsonSerializer.Deserialize<HotkeyDefinition>(json, SerializerOptions);
        if (legacyHotkey is null)
        {
            return LuvLetterConfiguration.Default;
        }

        var defaults = LuvLetterConfiguration.Default;
        return defaults with
        {
            InputBox = defaults.InputBox with
            {
                Hotkeys = defaults.InputBox.Hotkeys with
                {
                    Activation = NormalizeHotkey(
                        legacyHotkey,
                        defaults.InputBox.Hotkeys.Activation),
                },
            },
        };
    }

    private void Save(LuvLetterConfiguration configuration)
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

    private static InputBoxPlacementOptions NormalizePlacement(
        InputBoxPlacementOptions value,
        InputBoxPlacementOptions fallback)
    {
        return value with
        {
            Mode = Enum.IsDefined(value.Mode) ? value.Mode : fallback.Mode,
            OffsetX = Math.Clamp(value.OffsetX, -32768, 32768),
            OffsetY = Math.Clamp(value.OffsetY, -32768, 32768),
            BottomMargin = Math.Clamp(value.BottomMargin, 0, 4096),
            CustomX = Math.Clamp(value.CustomX, -32768, 32768),
            CustomY = Math.Clamp(value.CustomY, -32768, 32768),
        };
    }

    private static InputBoxColorOptions NormalizeInputBoxColors(
        InputBoxColorOptions value,
        InputBoxColorOptions fallback)
    {
        var opacity = NormalizeFinite(value.BackgroundOpacity, fallback.BackgroundOpacity, 0.0f, 1.0f);
        return value with
        {
            Border = NormalizeColor(value.Border, fallback.Border),
            Background = ApplyOpacityToColor(
                NormalizeColor(value.Background, fallback.Background),
                opacity),
            BackgroundOpacity = opacity,
            Text = NormalizeColor(value.Text, fallback.Text),
            Caret = NormalizeColor(value.Caret, fallback.Caret),
        };
    }

    private static InputBoxSizeOptions NormalizeInputBoxSize(
        InputBoxSizeOptions value,
        InputBoxSizeOptions fallback)
    {
        var width = Math.Clamp(value.Width, 120, 7680);
        var height = Math.Clamp(value.Height, 24, 512);
        return value with
        {
            Width = width,
            Height = height,
            CornerRadius = NormalizeFinite(
                value.CornerRadius,
                fallback.CornerRadius,
                0.0f,
                height / 2.0f),
            BorderThickness = NormalizeFinite(
                value.BorderThickness,
                fallback.BorderThickness,
                0.0f,
                Math.Min(16.0f, height / 2.0f)),
            FontSize = NormalizeFinite(value.FontSize, fallback.FontSize, 8.0f, 256.0f),
            HorizontalPadding = NormalizeFinite(
                value.HorizontalPadding,
                fallback.HorizontalPadding,
                0.0f,
                width / 2.0f),
            VerticalPadding = NormalizeFinite(
                value.VerticalPadding,
                fallback.VerticalPadding,
                0.0f,
                height / 2.0f),
            CaretWidth = NormalizeFinite(value.CaretWidth, fallback.CaretWidth, 0.5f, 16.0f),
        };
    }

    private static ActivationGestureOptions NormalizeActivationGestures(
        ActivationGestureOptions value,
        ActivationGestureOptions fallback)
    {
        var tapDuration = Math.Clamp(value.TapMaxDurationMs, 50, 1000);
        var secondPressTimeout = Math.Max(
            tapDuration,
            Math.Clamp(value.SecondPressTimeoutMs, 100, 5000));
        var holdThreshold = Math.Max(
            tapDuration + 1,
            Math.Clamp(value.HoldThresholdMs, 100, 10000));
        var allowLeftControl = value.AllowLeftControl;
        var allowRightControl = value.AllowRightControl;
        if (!allowLeftControl && !allowRightControl)
        {
            allowLeftControl = fallback.AllowLeftControl;
            allowRightControl = fallback.AllowRightControl;
        }

        var inputBoxGesture = Enum.IsDefined(value.InputBox)
            ? value.InputBox
            : fallback.InputBox;
        var featureWindowGesture = Enum.IsDefined(value.FeatureWindow)
            ? value.FeatureWindow
            : fallback.FeatureWindow;
        if (featureWindowGesture == inputBoxGesture)
        {
            featureWindowGesture = inputBoxGesture == ActivationGestureKind.DoubleControlPress
                ? ActivationGestureKind.ControlTapThenHold
                : ActivationGestureKind.DoubleControlPress;
        }

        return value with
        {
            InputBox = inputBoxGesture,
            FeatureWindow = featureWindowGesture,
            TapMaxDurationMs = tapDuration,
            SecondPressTimeoutMs = secondPressTimeout,
            HoldThresholdMs = holdThreshold,
            AllowLeftControl = allowLeftControl,
            AllowRightControl = allowRightControl,
        };
    }

    private static FeatureWindowLayoutOptions NormalizeFeatureLayout(
        FeatureWindowLayoutOptions value,
        FeatureWindowLayoutOptions fallback)
    {
        var cellSize = NormalizeFinite(value.CellSize, fallback.CellSize, 32.0f, 512.0f);
        return value with
        {
            ItemsPerPage = Math.Clamp(
                value.ItemsPerPage,
                1,
                FeatureWindowLayoutOptions.MaximumItemsPerPage),
            CellSize = cellSize,
            Gap = NormalizeFinite(value.Gap, fallback.Gap, 0.0f, 128.0f),
            CornerRadius = NormalizeFinite(
                value.CornerRadius,
                fallback.CornerRadius,
                0.0f,
                cellSize / 2.0f),
            BorderThickness = NormalizeFinite(
                value.BorderThickness,
                fallback.BorderThickness,
                0.0f,
                Math.Min(16.0f, cellSize / 2.0f)),
            FontSize = NormalizeFinite(value.FontSize, fallback.FontSize, 8.0f, 128.0f),
            BottomMargin = Math.Clamp(value.BottomMargin, 0, 4096),
            OffsetX = Math.Clamp(value.OffsetX, -32768, 32768),
            OffsetY = Math.Clamp(value.OffsetY, -32768, 32768),
        };
    }

    private static FeatureWindowColorOptions NormalizeFeatureColors(
        FeatureWindowColorOptions value,
        FeatureWindowColorOptions fallback)
    {
        var opacity = NormalizeFinite(value.BackgroundOpacity, fallback.BackgroundOpacity, 0.0f, 1.0f);
        return value with
        {
            Border = NormalizeColor(value.Border, fallback.Border),
            Background = ApplyOpacityToColor(
                NormalizeColor(value.Background, fallback.Background),
                opacity),
            BackgroundOpacity = opacity,
            Text = NormalizeColor(value.Text, fallback.Text),
            Accent = NormalizeColor(value.Accent, fallback.Accent),
        };
    }

    private static FeatureWindowHotkeyOptions NormalizeFeatureHotkeys(
        FeatureWindowHotkeyOptions value,
        FeatureWindowHotkeyOptions fallback,
        int itemsPerPage)
    {
        var maximumBaseKey = 0x39 - itemsPerPage + 1;
        var firstItemVirtualKey = value.FirstItemVirtualKey;
        if (firstItemVirtualKey is < 0x30 or > 0x39 || firstItemVirtualKey > maximumBaseKey)
        {
            firstItemVirtualKey = fallback.FirstItemVirtualKey;
        }

        var previousPage = NormalizeHotkey(value.PreviousPage, fallback.PreviousPage);
        var nextPage = NormalizeHotkey(value.NextPage, fallback.NextPage);
        var cancel = NormalizeHotkey(value.Cancel, fallback.Cancel);

        if (HotkeyEquals(previousPage, nextPage)
            || HotkeyEquals(previousPage, cancel)
            || HotkeyEquals(nextPage, cancel)
            || ConflictsWithFeatureNumbers(previousPage, firstItemVirtualKey, itemsPerPage)
            || ConflictsWithFeatureNumbers(nextPage, firstItemVirtualKey, itemsPerPage)
            || ConflictsWithFeatureNumbers(cancel, firstItemVirtualKey, itemsPerPage))
        {
            // Ambiguous bindings make one action unreachable because Native performs
            // deterministic first-match dispatch. Recover external/legacy JSON as a set.
            previousPage = fallback.PreviousPage;
            nextPage = fallback.NextPage;
            cancel = fallback.Cancel;
        }

        return value with
        {
            PreviousPage = previousPage,
            NextPage = nextPage,
            Cancel = cancel,
            FirstItemVirtualKey = firstItemVirtualKey,
        };
    }

    private static bool HotkeyEquals(HotkeyDefinition left, HotkeyDefinition right) =>
        left.VirtualKey == right.VirtualKey && left.Modifiers == right.Modifiers;

    private static bool ConflictsWithFeatureNumbers(
        HotkeyDefinition hotkey,
        int firstItemVirtualKey,
        int itemsPerPage) =>
        hotkey.Modifiers == HotkeyModifierKeys.None
        && hotkey.VirtualKey >= firstItemVirtualKey
        && hotkey.VirtualKey < firstItemVirtualKey + itemsPerPage;

    private static HotkeyDefinition NormalizeHotkey(
        HotkeyDefinition? value,
        HotkeyDefinition fallback)
    {
        if (value is null
            || (value.Modifiers & ~ValidModifiers) != HotkeyModifierKeys.None
            || value.VirtualKey is < 1 or > 0xFE)
        {
            return fallback;
        }

        var name = string.IsNullOrWhiteSpace(value.KeyName)
            ? $"VK {value.VirtualKey}"
            : value.KeyName.Trim();
        if (name.Length > 64)
        {
            name = name[..64];
        }

        return value with { KeyName = name };
    }

    private static float NormalizeFinite(float value, float fallback, float minimum, float maximum)
    {
        return float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        return hex.Length == 8
            && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? $"#{hex.ToUpperInvariant()}"
            : fallback;
    }

    private static string ApplyOpacityToColor(string value, float opacity)
    {
        var alpha = (int)Math.Round(opacity * 255.0f);
        return $"#{alpha:X2}{value[^6..]}";
    }

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
