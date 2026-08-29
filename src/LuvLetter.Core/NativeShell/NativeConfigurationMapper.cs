using System.Globalization;
using System.Runtime.InteropServices;
using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.NativeShell;

internal static class NativeConfigurationMapper
{
    public static NativeConfigurationPair Map(
        InputBoxConfiguration inputBoxConfiguration,
        QuickActionsConfiguration quickActionsConfiguration,
        uint abiVersion)
    {
        var normalized = LuvLetterConfigurationStore.Normalize(
            LuvLetterConfiguration.Default with
            {
                InputBox = inputBoxConfiguration,
                QuickActions = quickActionsConfiguration,
            });

        var inputBox = normalized.InputBox;
        var nativeInputConfig = new NativeInputBoxConfig
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeInputBoxConfig>()),
            AbiVersion = abiVersion,
            Width = inputBox.Size.Width,
            Height = inputBox.Size.Height,
            CornerRadius = inputBox.Size.CornerRadius,
            BorderThickness = inputBox.Size.BorderThickness,
            FontSize = SurfaceStyleDefaults.FontSize,
            HorizontalPadding = inputBox.Size.HorizontalPadding,
            VerticalPadding = inputBox.Size.VerticalPadding,
            CaretWidth = inputBox.Size.CaretWidth,
            PositionMode = (int)inputBox.Placement.Mode,
            OffsetX = inputBox.Placement.OffsetX,
            OffsetY = inputBox.Placement.OffsetY,
            BottomMargin = inputBox.Placement.BottomMargin,
            CustomX = inputBox.Placement.CustomX,
            CustomY = inputBox.Placement.CustomY,
            BorderColor = ParseArgb(inputBox.Colors.Border, SurfaceStyleDefaults.BorderArgb),
            BackgroundColor = ApplyOpacity(
                ParseArgb(inputBox.Colors.Background, SurfaceStyleDefaults.BackgroundArgb),
                inputBox.Colors.BackgroundOpacity),
            TextColor = MultiplyOpacity(
                ParseArgb(inputBox.Colors.Text, SurfaceStyleDefaults.ContentArgb),
                inputBox.Colors.TextOpacity),
            CaretColor = ParseArgb(inputBox.Colors.Caret, SurfaceStyleDefaults.ContentArgb),
            SubmitVirtualKey = inputBox.Hotkeys.Submit.VirtualKey,
            CancelVirtualKey = inputBox.Hotkeys.Cancel.VirtualKey,
            BackspaceVirtualKey = inputBox.Hotkeys.Backspace.VirtualKey,
            SubmitModifiers = (int)inputBox.Hotkeys.Submit.Modifiers,
            CancelModifiers = (int)inputBox.Hotkeys.Cancel.Modifiers,
            BackspaceModifiers = (int)inputBox.Hotkeys.Backspace.Modifiers,
        };

        var quickActions = normalized.QuickActions;
        var nativeFeatureConfig = new NativeFeatureWindowConfig
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeFeatureWindowConfig>()),
            AbiVersion = abiVersion,
            ItemsPerPage = quickActions.Layout.ItemsPerPage,
            CellSize = quickActions.Layout.CellSize,
            Gap = quickActions.Layout.Gap,
            CornerRadius = quickActions.Layout.CornerRadius,
            BorderThickness = quickActions.Layout.BorderThickness,
            FontSize = SurfaceStyleDefaults.FontSize,
            BottomMargin = quickActions.Layout.BottomMargin,
            OffsetX = quickActions.Layout.OffsetX,
            OffsetY = quickActions.Layout.OffsetY,
            BorderColor = ParseArgb(
                quickActions.Colors.Border,
                SurfaceStyleDefaults.BorderArgb),
            BackgroundColor = ApplyOpacity(
                ParseArgb(
                    quickActions.Colors.Background,
                    SurfaceStyleDefaults.BackgroundArgb),
                quickActions.Colors.BackgroundOpacity),
            TextColor = MultiplyOpacity(
                ParseArgb(quickActions.Colors.Text, SurfaceStyleDefaults.ContentArgb),
                quickActions.Colors.TextOpacity),
            AccentColor = ParseArgb(
                quickActions.Colors.Accent,
                SurfaceStyleDefaults.ContentArgb),
            PreviousVirtualKey = quickActions.Hotkeys.PreviousPage.VirtualKey,
            NextVirtualKey = quickActions.Hotkeys.NextPage.VirtualKey,
            CancelVirtualKey = quickActions.Hotkeys.Cancel.VirtualKey,
            FirstItemVirtualKey = quickActions.Hotkeys.FirstItemVirtualKey,
            PreviousModifiers = (int)quickActions.Hotkeys.PreviousPage.Modifiers,
            NextModifiers = (int)quickActions.Hotkeys.NextPage.Modifiers,
            CancelModifiers = (int)quickActions.Hotkeys.Cancel.Modifiers,
        };

        return new(nativeInputConfig, nativeFeatureConfig);
    }

    private static uint ParseArgb(string? value, uint fallback)
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
            && uint.TryParse(
                hex,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var parsed)
            ? parsed
            : fallback;
    }

    private static uint ApplyOpacity(uint argb, float opacity)
    {
        var alpha = (uint)Math.Round(Math.Clamp(opacity, 0.0f, 1.0f) * 255.0f);
        return (argb & 0x00FFFFFF) | (alpha << 24);
    }

    private static uint MultiplyOpacity(uint argb, float opacity)
    {
        var sourceAlpha = (argb >> 24) & 0xFF;
        var alpha = (uint)Math.Round(sourceAlpha * Math.Clamp(opacity, 0.0f, 1.0f));
        return (argb & 0x00FFFFFF) | (alpha << 24);
    }
}

internal readonly record struct NativeConfigurationPair(
    NativeInputBoxConfig InputBox,
    NativeFeatureWindowConfig QuickActions);
