using System.Globalization;
using System.Runtime.InteropServices;
using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.Native;

internal static class NativeConfigurationMapper
{
    public static NativeConfigurationPair Map(
        InputBoxConfiguration inputBoxConfiguration,
        FeatureWindowConfiguration featureWindowConfiguration,
        uint abiVersion)
    {
        var normalized = LuvLetterConfigurationStore.Normalize(
            LuvLetterConfiguration.Default with
            {
                InputBox = inputBoxConfiguration,
                FeatureWindow = featureWindowConfiguration,
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
            FontSize = inputBox.Size.FontSize,
            HorizontalPadding = inputBox.Size.HorizontalPadding,
            VerticalPadding = inputBox.Size.VerticalPadding,
            CaretWidth = inputBox.Size.CaretWidth,
            PositionMode = (int)inputBox.Placement.Mode,
            OffsetX = inputBox.Placement.OffsetX,
            OffsetY = inputBox.Placement.OffsetY,
            BottomMargin = inputBox.Placement.BottomMargin,
            CustomX = inputBox.Placement.CustomX,
            CustomY = inputBox.Placement.CustomY,
            BorderColor = ParseArgb(inputBox.Colors.Border, 0x66FFFFFF),
            BackgroundColor = ApplyOpacity(
                ParseArgb(inputBox.Colors.Background, 0x80F5F5F5),
                inputBox.Colors.BackgroundOpacity),
            TextColor = MultiplyOpacity(
                ParseArgb(inputBox.Colors.Text, 0xFFFFFFFF),
                inputBox.Colors.TextOpacity),
            CaretColor = ParseArgb(inputBox.Colors.Caret, 0xFFFFFFFF),
            SubmitVirtualKey = inputBox.Hotkeys.Submit.VirtualKey,
            CancelVirtualKey = inputBox.Hotkeys.Cancel.VirtualKey,
            BackspaceVirtualKey = inputBox.Hotkeys.Backspace.VirtualKey,
            SubmitModifiers = (int)inputBox.Hotkeys.Submit.Modifiers,
            CancelModifiers = (int)inputBox.Hotkeys.Cancel.Modifiers,
            BackspaceModifiers = (int)inputBox.Hotkeys.Backspace.Modifiers,
        };

        var featureWindow = normalized.FeatureWindow;
        var nativeFeatureConfig = new NativeFeatureWindowConfig
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeFeatureWindowConfig>()),
            AbiVersion = abiVersion,
            ItemsPerPage = featureWindow.Layout.ItemsPerPage,
            CellSize = featureWindow.Layout.CellSize,
            Gap = featureWindow.Layout.Gap,
            CornerRadius = featureWindow.Layout.CornerRadius,
            BorderThickness = featureWindow.Layout.BorderThickness,
            FontSize = featureWindow.Layout.FontSize,
            BottomMargin = featureWindow.Layout.BottomMargin,
            OffsetX = featureWindow.Layout.OffsetX,
            OffsetY = featureWindow.Layout.OffsetY,
            BorderColor = ParseArgb(featureWindow.Colors.Border, 0x66FFFFFF),
            BackgroundColor = ApplyOpacity(
                ParseArgb(featureWindow.Colors.Background, 0x38F5F5F5),
                featureWindow.Colors.BackgroundOpacity),
            TextColor = MultiplyOpacity(
                ParseArgb(featureWindow.Colors.Text, 0xFFFFFFFF),
                featureWindow.Colors.TextOpacity),
            AccentColor = ParseArgb(featureWindow.Colors.Accent, 0xFFFFFFFF),
            PreviousVirtualKey = featureWindow.Hotkeys.PreviousPage.VirtualKey,
            NextVirtualKey = featureWindow.Hotkeys.NextPage.VirtualKey,
            CancelVirtualKey = featureWindow.Hotkeys.Cancel.VirtualKey,
            FirstItemVirtualKey = featureWindow.Hotkeys.FirstItemVirtualKey,
            PreviousModifiers = (int)featureWindow.Hotkeys.PreviousPage.Modifiers,
            NextModifiers = (int)featureWindow.Hotkeys.NextPage.Modifiers,
            CancelModifiers = (int)featureWindow.Hotkeys.Cancel.Modifiers,
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
    NativeFeatureWindowConfig FeatureWindow);
