using System.Globalization;
using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.Native;

public sealed class InputBoxService : IInputBoxService
{
    public void ApplyConfiguration(InputBoxConfiguration configuration)
    {
        var nativeConfig = new NativeInputBoxConfig
        {
            Width = Math.Max(1, configuration.Size.Width),
            Height = Math.Max(1, configuration.Size.Height),
            CornerRadius = Math.Max(0.0f, configuration.Size.CornerRadius),
            BorderThickness = Math.Max(0.0f, configuration.Size.BorderThickness),
            FontSize = Math.Max(1.0f, configuration.Size.FontSize),
            HorizontalPadding = Math.Max(0.0f, configuration.Size.HorizontalPadding),
            PositionMode = (int)configuration.Placement.Mode,
            OffsetX = configuration.Placement.OffsetX,
            OffsetY = configuration.Placement.OffsetY,
            BottomMargin = Math.Max(0, configuration.Placement.BottomMargin),
            CustomX = configuration.Placement.CustomX,
            CustomY = configuration.Placement.CustomY,
            BorderColor = ParseArgb(configuration.Colors.Border, 0xFFFFFFFF),
            BackgroundColor = ParseArgb(configuration.Colors.Background, 0x66DCDCDC),
            TextColor = ParseArgb(configuration.Colors.Text, 0xF2191919),
            CaretColor = ParseArgb(configuration.Colors.Caret, 0xF2191919),
            SubmitVirtualKey = configuration.Hotkeys.Submit.VirtualKey,
            CancelVirtualKey = configuration.Hotkeys.Cancel.VirtualKey,
            BackspaceVirtualKey = configuration.Hotkeys.Backspace.VirtualKey,
        };

        NativeInputBoxApi.ThrowIfFailed(NativeInputBoxApi.ApplyInputBoxConfig(in nativeConfig));
    }

    public void Show()
    {
        NativeInputBoxApi.ThrowIfFailed(NativeInputBoxApi.ShowInputBox());
    }

    public void Hide()
    {
        NativeInputBoxApi.ThrowIfFailed(NativeInputBoxApi.HideInputBox());
    }

    public void Toggle()
    {
        NativeInputBoxApi.ThrowIfFailed(NativeInputBoxApi.ToggleInputBox());
    }

    public void Dispose()
    {
        NativeInputBoxApi.ShutdownInputBox();
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

        return hex.Length == 8 && uint.TryParse(
            hex,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }
}
