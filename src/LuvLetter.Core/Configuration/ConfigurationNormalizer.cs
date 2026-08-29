using System.Globalization;
using LuvLetter.Core.Hotkeys;

namespace LuvLetter.Core.Configuration;

/// <summary>
/// Applies runtime invariants and fallback values to configuration data.
/// </summary>
internal static class ConfigurationNormalizer
{
    private const HotkeyModifierKeys ValidModifiers =
        HotkeyModifierKeys.Alt
        | HotkeyModifierKeys.Control
        | HotkeyModifierKeys.Shift
        | HotkeyModifierKeys.Win;

    internal static LuvLetterConfiguration Normalize(LuvLetterConfiguration? configuration)
    {
        var defaults = LuvLetterConfiguration.Default;
        configuration ??= defaults;
        configuration = ConfigurationSchemaMigrator.Migrate(configuration, defaults);

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

        var quickActions = configuration.QuickActions ?? defaults.QuickActions;
        var quickActionsLayout = NormalizeQuickActionsLayout(
            quickActions.Layout ?? defaults.QuickActions.Layout,
            defaults.QuickActions.Layout);
        var quickActionsColors = NormalizeQuickActionsColors(
            quickActions.Colors ?? defaults.QuickActions.Colors,
            defaults.QuickActions.Colors);
        var quickActionsHotkeys = NormalizeQuickActionsHotkeys(
            quickActions.Hotkeys ?? defaults.QuickActions.Hotkeys,
            defaults.QuickActions.Hotkeys,
            quickActionsLayout.ItemsPerPage);

        return configuration with
        {
            SchemaVersion = LuvLetterConfiguration.CurrentSchemaVersion,
            InputBox = inputBox with
            {
                Hotkeys = hotkeys with
                {
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
            QuickActions = quickActions with
            {
                Layout = quickActionsLayout,
                Colors = quickActionsColors,
                Hotkeys = quickActionsHotkeys,
            },
        };
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
        var textOpacity = NormalizeFinite(value.TextOpacity, fallback.TextOpacity, 0.0f, 1.0f);
        return value with
        {
            Border = NormalizeColor(value.Border, fallback.Border),
            Background = ApplyOpacityToColor(
                NormalizeColor(value.Background, fallback.Background),
                opacity),
            BackgroundOpacity = opacity,
            Text = NormalizeColor(value.Text, fallback.Text),
            TextOpacity = textOpacity,
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
            FontSize = SurfaceStyleDefaults.FontSize,
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

        return value with
        {
            // Schema v6 and earlier allowed swapping two Ctrl gestures. The runtime
            // now reserves double Ctrl for command input and Alt+F1 for Quick Actions.
            InputBox = ActivationGestureKind.DoubleControlPress,
            QuickActions = ActivationGestureKind.ControlTapThenHold,
            TapMaxDurationMs = tapDuration,
            SecondPressTimeoutMs = secondPressTimeout,
            HoldThresholdMs = holdThreshold,
            AllowLeftControl = allowLeftControl,
            AllowRightControl = allowRightControl,
        };
    }

    private static QuickActionsLayoutOptions NormalizeQuickActionsLayout(
        QuickActionsLayoutOptions value,
        QuickActionsLayoutOptions fallback)
    {
        var cellSize = NormalizeFinite(value.CellSize, fallback.CellSize, 32.0f, 512.0f);
        return value with
        {
            ItemsPerPage = Math.Clamp(
                value.ItemsPerPage,
                1,
                QuickActionsLayoutOptions.MaximumItemsPerPage),
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
            FontSize = SurfaceStyleDefaults.FontSize,
            BottomMargin = Math.Clamp(value.BottomMargin, 0, 4096),
            OffsetX = Math.Clamp(value.OffsetX, -32768, 32768),
            OffsetY = Math.Clamp(value.OffsetY, -32768, 32768),
        };
    }

    private static QuickActionsColorOptions NormalizeQuickActionsColors(
        QuickActionsColorOptions value,
        QuickActionsColorOptions fallback)
    {
        var opacity = NormalizeFinite(value.BackgroundOpacity, fallback.BackgroundOpacity, 0.0f, 1.0f);
        var textOpacity = NormalizeFinite(value.TextOpacity, fallback.TextOpacity, 0.0f, 1.0f);
        return value with
        {
            Border = NormalizeColor(value.Border, fallback.Border),
            Background = ApplyOpacityToColor(
                NormalizeColor(value.Background, fallback.Background),
                opacity),
            BackgroundOpacity = opacity,
            Text = NormalizeColor(value.Text, fallback.Text),
            TextOpacity = textOpacity,
            Accent = NormalizeColor(value.Accent, fallback.Accent),
        };
    }

    private static QuickActionsHotkeyOptions NormalizeQuickActionsHotkeys(
        QuickActionsHotkeyOptions value,
        QuickActionsHotkeyOptions fallback,
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

        var normalizedHotkeys = value with
        {
            PreviousPage = previousPage,
            NextPage = nextPage,
            Cancel = cancel,
            FirstItemVirtualKey = firstItemVirtualKey,
        };
        if (QuickActionHotkeyRules.FindConflict(normalizedHotkeys, itemsPerPage)
            != QuickActionHotkeyConflict.None)
        {
            // Ambiguous bindings make one action unreachable because Native performs
            // deterministic first-match dispatch. Recover external/legacy JSON as a set.
            previousPage = fallback.PreviousPage;
            nextPage = fallback.NextPage;
            cancel = fallback.Cancel;
        }

        return normalizedHotkeys with
        {
            PreviousPage = previousPage,
            NextPage = nextPage,
            Cancel = cancel,
            FirstItemVirtualKey = firstItemVirtualKey,
        };
    }

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
}
