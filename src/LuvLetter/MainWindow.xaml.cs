using System.Globalization;
using System.Windows;
using System.Windows.Input;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Hotkeys;
using LuvLetter.Core.Native;
using LuvLetter.Hotkeys;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LuvLetter;

public partial class MainWindow : Window
{
    private readonly LuvLetterConfigurationStore configurationStore;
    private readonly GlobalHotkeyService hotkeyService;
    private readonly IInputBoxService inputBoxService;
    private LuvLetterConfiguration pendingConfiguration;

    public MainWindow(
        LuvLetterConfigurationStore configurationStore,
        GlobalHotkeyService hotkeyService,
        IInputBoxService inputBoxService
    )
    {
        this.configurationStore = configurationStore;
        this.hotkeyService = hotkeyService;
        this.inputBoxService = inputBoxService;
        pendingConfiguration = configurationStore.Current;

        InitializeComponent();
        PositionModeComboBox.ItemsSource = Enum.GetValues<InputBoxPositionMode>();
        ApplyConfigurationToControls(pendingConfiguration);
        SetStatus("Ready");
    }

    public void SetStatus(string text)
    {
        if (StatusTextBlock is not null)
        {
            StatusTextBlock.Text = text;
        }
    }

    private void HotkeyTextBox_OnPreviewKeyDown(object sender, WpfKeyEventArgs eventArgs)
    {
        eventArgs.Handled = true;

        if (!TryCreateHotkey(eventArgs, out var hotkey, out var error))
        {
            SetStatus(error);
            return;
        }

        var hotkeys = pendingConfiguration.InputBox.Hotkeys;
        hotkeys = sender switch
        {
            WpfTextBox textBox when ReferenceEquals(textBox, ActivationHotkeyTextBox) =>
                hotkey.Modifiers == HotkeyModifierKeys.None
                    ? hotkeys
                    : hotkeys with
                    {
                        Activation = hotkey,
                    },
            WpfTextBox textBox when ReferenceEquals(textBox, SubmitHotkeyTextBox) => hotkeys with
            {
                Submit = hotkey,
            },
            WpfTextBox textBox when ReferenceEquals(textBox, CancelHotkeyTextBox) => hotkeys with
            {
                Cancel = hotkey,
            },
            WpfTextBox textBox when ReferenceEquals(textBox, BackspaceHotkeyTextBox) => hotkeys with
            {
                Backspace = hotkey,
            },
            _ => hotkeys,
        };

        if (
            ReferenceEquals(sender, ActivationHotkeyTextBox)
            && hotkey.Modifiers == HotkeyModifierKeys.None
        )
        {
            SetStatus("Activation hotkey must include Alt, Ctrl, Shift, or Win");
            return;
        }

        pendingConfiguration = pendingConfiguration with
        {
            InputBox = pendingConfiguration.InputBox with { Hotkeys = hotkeys },
        };
        ApplyHotkeysToControls(pendingConfiguration.InputBox.Hotkeys);
        SetStatus("Pending");
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryReadConfigurationFromControls(out var nextConfiguration, out var error))
        {
            SetStatus(error);
            return;
        }

        if (!hotkeyService.TryUpdate(nextConfiguration.InputBox.Hotkeys.Activation, out error))
        {
            SetStatus(error ?? "Cannot register hotkey");
            return;
        }

        inputBoxService.ApplyConfiguration(nextConfiguration.InputBox);
        configurationStore.Update(nextConfiguration);
        pendingConfiguration = nextConfiguration;
        ApplyConfigurationToControls(pendingConfiguration);
        SetStatus("Applied");
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        pendingConfiguration = LuvLetterConfiguration.Default;
        ApplyConfigurationToControls(pendingConfiguration);
        SetStatus("Pending");
    }

    private void ApplyConfigurationToControls(LuvLetterConfiguration configuration)
    {
        ApplyHotkeysToControls(configuration.InputBox.Hotkeys);

        var placement = configuration.InputBox.Placement;
        PositionModeComboBox.SelectedItem = placement.Mode;
        OffsetXTextBox.Text = placement.OffsetX.ToString(CultureInfo.InvariantCulture);
        OffsetYTextBox.Text = placement.OffsetY.ToString(CultureInfo.InvariantCulture);
        BottomMarginTextBox.Text = placement.BottomMargin.ToString(CultureInfo.InvariantCulture);
        CustomXTextBox.Text = placement.CustomX.ToString(CultureInfo.InvariantCulture);
        CustomYTextBox.Text = placement.CustomY.ToString(CultureInfo.InvariantCulture);

        var colors = configuration.InputBox.Colors;
        BorderColorTextBox.Text = colors.Border;
        BackgroundColorTextBox.Text = colors.Background;
        TextColorTextBox.Text = colors.Text;
        CaretColorTextBox.Text = colors.Caret;

        var size = configuration.InputBox.Size;
        WidthTextBox.Text = size.Width.ToString(CultureInfo.InvariantCulture);
        HeightTextBox.Text = size.Height.ToString(CultureInfo.InvariantCulture);
        FontSizeTextBox.Text = size.FontSize.ToString(CultureInfo.InvariantCulture);
        CornerRadiusTextBox.Text = size.CornerRadius.ToString(CultureInfo.InvariantCulture);
        BorderThicknessTextBox.Text = size.BorderThickness.ToString(CultureInfo.InvariantCulture);
        HorizontalPaddingTextBox.Text = size.HorizontalPadding.ToString(
            CultureInfo.InvariantCulture
        );
    }

    private void ApplyHotkeysToControls(InputBoxHotkeyOptions hotkeys)
    {
        ActivationHotkeyTextBox.Text = hotkeys.Activation.DisplayText;
        SubmitHotkeyTextBox.Text = hotkeys.Submit.DisplayText;
        CancelHotkeyTextBox.Text = hotkeys.Cancel.DisplayText;
        BackspaceHotkeyTextBox.Text = hotkeys.Backspace.DisplayText;
    }

    private bool TryReadConfigurationFromControls(
        out LuvLetterConfiguration configuration,
        out string error
    )
    {
        configuration = pendingConfiguration;
        error = string.Empty;

        if (
            !TryReadInt(OffsetXTextBox, "Offset X", out var offsetX, out error)
            || !TryReadInt(OffsetYTextBox, "Offset Y", out var offsetY, out error)
            || !TryReadInt(BottomMarginTextBox, "Bottom margin", out var bottomMargin, out error)
            || !TryReadInt(CustomXTextBox, "Custom X", out var customX, out error)
            || !TryReadInt(CustomYTextBox, "Custom Y", out var customY, out error)
            || !TryReadInt(WidthTextBox, "Width", out var width, out error)
            || !TryReadInt(HeightTextBox, "Height", out var height, out error)
            || !TryReadFloat(FontSizeTextBox, "Font size", out var fontSize, out error)
            || !TryReadFloat(CornerRadiusTextBox, "Corner radius", out var cornerRadius, out error)
            || !TryReadFloat(
                BorderThicknessTextBox,
                "Border thickness",
                out var borderThickness,
                out error
            )
            || !TryReadFloat(
                HorizontalPaddingTextBox,
                "Horizontal padding",
                out var horizontalPadding,
                out error
            )
        )
        {
            return false;
        }

        if (
            !IsColorText(BorderColorTextBox.Text)
            || !IsColorText(BackgroundColorTextBox.Text)
            || !IsColorText(TextColorTextBox.Text)
            || !IsColorText(CaretColorTextBox.Text)
        )
        {
            error = "Colors must be #RRGGBB or #AARRGGBB";
            return false;
        }

        configuration = pendingConfiguration with
        {
            InputBox = pendingConfiguration.InputBox with
            {
                Placement = new InputBoxPlacementOptions
                {
                    Mode = PositionModeComboBox.SelectedItem is InputBoxPositionMode mode
                        ? mode
                        : InputBoxPositionMode.CenterBottom,
                    OffsetX = offsetX,
                    OffsetY = offsetY,
                    BottomMargin = bottomMargin,
                    CustomX = customX,
                    CustomY = customY,
                },
                Colors = new InputBoxColorOptions
                {
                    Border = BorderColorTextBox.Text.Trim(),
                    Background = BackgroundColorTextBox.Text.Trim(),
                    Text = TextColorTextBox.Text.Trim(),
                    Caret = CaretColorTextBox.Text.Trim(),
                },
                Size = new InputBoxSizeOptions
                {
                    Width = width,
                    Height = height,
                    FontSize = fontSize,
                    CornerRadius = cornerRadius,
                    BorderThickness = borderThickness,
                    HorizontalPadding = horizontalPadding,
                },
            },
        };

        return true;
    }

    private static bool TryReadInt(
        WpfTextBox textBox,
        string label,
        out int value,
        out string error
    )
    {
        if (
            int.TryParse(
                textBox.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value
            )
        )
        {
            error = string.Empty;
            return true;
        }

        error = $"{label} must be an integer";
        return false;
    }

    private static bool TryReadFloat(
        WpfTextBox textBox,
        string label,
        out float value,
        out string error
    )
    {
        if (
            float.TryParse(
                textBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value
            )
        )
        {
            error = string.Empty;
            return true;
        }

        error = $"{label} must be a number";
        return false;
    }

    private static bool IsColorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = text.Trim().TrimStart('#');
        return hex.Length is 6 or 8
            && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    }

    private static bool TryCreateHotkey(
        WpfKeyEventArgs eventArgs,
        out HotkeyDefinition hotkey,
        out string error
    )
    {
        hotkey = HotkeyDefinition.Default;
        error = string.Empty;

        var key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        key = key == Key.ImeProcessed ? eventArgs.ImeProcessedKey : key;
        if (IsModifierKey(key))
        {
            error = "Press a non-modifier key";
            return false;
        }

        var modifiers = GetCurrentModifiers();
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            error = "Unsupported key";
            return false;
        }

        hotkey = new HotkeyDefinition(modifiers, virtualKey, NormalizeKeyName(key));
        return true;
    }

    private static HotkeyModifierKeys GetCurrentModifiers()
    {
        var keyboardModifiers = Keyboard.Modifiers;
        var modifiers = HotkeyModifierKeys.None;

        if (keyboardModifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifierKeys.Alt;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifierKeys.Control;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifierKeys.Shift;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifierKeys.Win;
        }

        return modifiers;
    }

    private static bool IsModifierKey(Key key)
    {
        return key
            is Key.LeftAlt
                or Key.RightAlt
                or Key.LeftCtrl
                or Key.RightCtrl
                or Key.LeftShift
                or Key.RightShift
                or Key.LWin
                or Key.RWin
                or Key.System;
    }

    private static string NormalizeKeyName(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return $"NumPad{(int)(key - Key.NumPad0)}";
        }

        return key.ToString();
    }
}
