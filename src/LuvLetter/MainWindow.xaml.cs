using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    private readonly IReadOnlyList<GestureChoice> gestureChoices =
    [
        new(ActivationGestureKind.DoubleControlPress, "Double-tap Ctrl"),
        new(ActivationGestureKind.ControlTapThenHold, "Tap Ctrl, then hold Ctrl"),
    ];
    private readonly ObservableCollection<FirstItemKeyChoice> firstItemKeyChoices = new(
        Enumerable.Range(0, 10).Select(
            digit => new FirstItemKeyChoice(
                0x30 + digit,
                digit.ToString(CultureInfo.InvariantCulture)
            )
        )
    );

    private LuvLetterConfiguration pendingConfiguration;
    private bool isApplyingConfiguration = true;

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
        InputBoxGestureComboBox.ItemsSource = gestureChoices;
        FeatureWindowGestureComboBox.ItemsSource = gestureChoices;
        FirstItemNumberComboBox.ItemsSource = firstItemKeyChoices;

        isApplyingConfiguration = false;
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

    private void ConfigurationTextBox_OnTextChanged(
        object sender,
        TextChangedEventArgs eventArgs
    ) => MarkPending();

    private void ConfigurationComboBox_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs
    ) => MarkPending();

    private void ConfigurationCheckBox_OnChanged(object sender, RoutedEventArgs eventArgs) =>
        MarkPending();

    private void MarkPending()
    {
        if (!isApplyingConfiguration)
        {
            SetStatus("Pending");
        }
    }

    private void HotkeyTextBox_OnPreviewKeyDown(object sender, WpfKeyEventArgs eventArgs)
    {
        var key = GetEventKey(eventArgs);
        if (key == Key.Tab)
        {
            // Keep normal Tab and Shift+Tab focus traversal in read-only hotkey fields.
            return;
        }

        eventArgs.Handled = true;
        if (!TryCreateHotkey(eventArgs, out var hotkey, out var error))
        {
            SetStatus(error);
            return;
        }

        var inputHotkeys = pendingConfiguration.InputBox.Hotkeys;
        var featureHotkeys = pendingConfiguration.FeatureWindow.Hotkeys;
        var inputChanged = true;
        var featureChanged = false;

        if (ReferenceEquals(sender, SubmitHotkeyTextBox))
        {
            inputHotkeys = inputHotkeys with { Submit = hotkey };
        }
        else if (ReferenceEquals(sender, CancelHotkeyTextBox))
        {
            inputHotkeys = inputHotkeys with { Cancel = hotkey };
        }
        else if (ReferenceEquals(sender, BackspaceHotkeyTextBox))
        {
            inputHotkeys = inputHotkeys with { Backspace = hotkey };
        }
        else
        {
            inputChanged = false;
            featureChanged = true;

            if (ReferenceEquals(sender, FeaturePreviousPageHotkeyTextBox))
            {
                featureHotkeys = featureHotkeys with { PreviousPage = hotkey };
            }
            else if (ReferenceEquals(sender, FeatureNextPageHotkeyTextBox))
            {
                featureHotkeys = featureHotkeys with { NextPage = hotkey };
            }
            else if (ReferenceEquals(sender, FeatureCancelHotkeyTextBox))
            {
                featureHotkeys = featureHotkeys with { Cancel = hotkey };
            }
            else
            {
                featureChanged = false;
            }
        }

        if (!inputChanged && !featureChanged)
        {
            SetStatus("Unknown hotkey field");
            return;
        }

        pendingConfiguration = pendingConfiguration with
        {
            InputBox = inputChanged
                ? pendingConfiguration.InputBox with { Hotkeys = inputHotkeys }
                : pendingConfiguration.InputBox,
            FeatureWindow = featureChanged
                ? pendingConfiguration.FeatureWindow with { Hotkeys = featureHotkeys }
                : pendingConfiguration.FeatureWindow,
        };

        RefreshHotkeyControls();
        SetStatus("Pending");
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryReadConfigurationFromControls(out var rawConfiguration, out var error))
        {
            SetStatus(error);
            return;
        }

        LuvLetterConfiguration normalizedConfiguration;
        try
        {
            normalizedConfiguration = LuvLetterConfigurationStore.Normalize(rawConfiguration);
        }
        catch (Exception exception)
        {
            SetStatus($"Cannot normalize settings: {exception.Message}");
            return;
        }

        var previousConfiguration = configurationStore.Current;
        LuvLetterConfiguration appliedConfiguration;
        try
        {
            inputBoxService.ApplyConfiguration(
                normalizedConfiguration.InputBox,
                normalizedConfiguration.FeatureWindow
            );
            hotkeyService.Update(normalizedConfiguration.ActivationGestures);
            appliedConfiguration = configurationStore.Update(normalizedConfiguration);
        }
        catch (Exception exception)
        {
            var rollbackError = TryRollback(previousConfiguration);
            pendingConfiguration = previousConfiguration;
            ApplyConfigurationToControls(previousConfiguration);

            SetStatus(
                rollbackError is null
                    ? $"Apply failed: {exception.Message}. Previous settings restored."
                    : $"Apply failed: {exception.Message}. Rollback warning: {rollbackError}"
            );
            return;
        }

        pendingConfiguration = appliedConfiguration;
        ApplyConfigurationToControls(appliedConfiguration);
        SetStatus("Applied");
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        pendingConfiguration = LuvLetterConfigurationStore.Normalize(
            LuvLetterConfiguration.Default
        );
        ApplyConfigurationToControls(pendingConfiguration);
        SetStatus("Pending");
    }

    private string? TryRollback(LuvLetterConfiguration previousConfiguration)
    {
        var failures = new List<string>();

        try
        {
            hotkeyService.Update(previousConfiguration.ActivationGestures);
        }
        catch (Exception exception)
        {
            failures.Add($"gesture: {exception.Message}");
        }

        try
        {
            inputBoxService.ApplyConfiguration(
                previousConfiguration.InputBox,
                previousConfiguration.FeatureWindow
            );
        }
        catch (Exception exception)
        {
            failures.Add($"native windows: {exception.Message}");
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private void ApplyConfigurationToControls(LuvLetterConfiguration configuration)
    {
        RunWithoutPendingNotification(() =>
        {
            ApplyHotkeysToControls(configuration);

            var gestures = configuration.ActivationGestures;
            InputBoxGestureComboBox.SelectedItem = gestureChoices.First(
                choice => choice.Kind == gestures.InputBox
            );
            FeatureWindowGestureComboBox.SelectedItem = gestureChoices.First(
                choice => choice.Kind == gestures.FeatureWindow
            );
            TapMaxDurationTextBox.Text = gestures.TapMaxDurationMs.ToString(
                CultureInfo.InvariantCulture
            );
            SecondPressTimeoutTextBox.Text = gestures.SecondPressTimeoutMs.ToString(
                CultureInfo.InvariantCulture
            );
            HoldThresholdTextBox.Text = gestures.HoldThresholdMs.ToString(
                CultureInfo.InvariantCulture
            );
            AllowLeftControlCheckBox.IsChecked = gestures.AllowLeftControl;
            AllowRightControlCheckBox.IsChecked = gestures.AllowRightControl;

            var placement = configuration.InputBox.Placement;
            PositionModeComboBox.SelectedItem = placement.Mode;
            OffsetXTextBox.Text = placement.OffsetX.ToString(CultureInfo.InvariantCulture);
            OffsetYTextBox.Text = placement.OffsetY.ToString(CultureInfo.InvariantCulture);
            BottomMarginTextBox.Text = placement.BottomMargin.ToString(
                CultureInfo.InvariantCulture
            );
            CustomXTextBox.Text = placement.CustomX.ToString(CultureInfo.InvariantCulture);
            CustomYTextBox.Text = placement.CustomY.ToString(CultureInfo.InvariantCulture);

            var inputColors = configuration.InputBox.Colors;
            BorderColorTextBox.Text = inputColors.Border;
            BackgroundColorTextBox.Text = inputColors.Background;
            BackgroundOpacityTextBox.Text = inputColors.BackgroundOpacity.ToString(
                CultureInfo.InvariantCulture
            );
            TextColorTextBox.Text = inputColors.Text;
            CaretColorTextBox.Text = inputColors.Caret;

            var inputSize = configuration.InputBox.Size;
            WidthTextBox.Text = inputSize.Width.ToString(CultureInfo.InvariantCulture);
            HeightTextBox.Text = inputSize.Height.ToString(CultureInfo.InvariantCulture);
            FontSizeTextBox.Text = inputSize.FontSize.ToString(CultureInfo.InvariantCulture);
            CornerRadiusTextBox.Text = inputSize.CornerRadius.ToString(
                CultureInfo.InvariantCulture
            );
            BorderThicknessTextBox.Text = inputSize.BorderThickness.ToString(
                CultureInfo.InvariantCulture
            );
            HorizontalPaddingTextBox.Text = inputSize.HorizontalPadding.ToString(
                CultureInfo.InvariantCulture
            );
            VerticalPaddingTextBox.Text = inputSize.VerticalPadding.ToString(
                CultureInfo.InvariantCulture
            );
            CaretWidthTextBox.Text = inputSize.CaretWidth.ToString(
                CultureInfo.InvariantCulture
            );

            var featureLayout = configuration.FeatureWindow.Layout;
            FeatureItemsPerPageTextBox.Text = featureLayout.ItemsPerPage.ToString(
                CultureInfo.InvariantCulture
            );
            FeatureCellSizeTextBox.Text = featureLayout.CellSize.ToString(
                CultureInfo.InvariantCulture
            );
            FeatureGapTextBox.Text = featureLayout.Gap.ToString(CultureInfo.InvariantCulture);
            FeatureCornerRadiusTextBox.Text = featureLayout.CornerRadius.ToString(
                CultureInfo.InvariantCulture
            );
            FeatureBorderThicknessTextBox.Text = featureLayout.BorderThickness.ToString(
                CultureInfo.InvariantCulture
            );
            FeatureFontSizeTextBox.Text = featureLayout.FontSize.ToString(
                CultureInfo.InvariantCulture
            );
            FeatureBottomMarginTextBox.Text = featureLayout.BottomMargin.ToString(
                CultureInfo.InvariantCulture
            );
            FeatureOffsetXTextBox.Text = featureLayout.OffsetX.ToString(
                CultureInfo.InvariantCulture
            );
            FeatureOffsetYTextBox.Text = featureLayout.OffsetY.ToString(
                CultureInfo.InvariantCulture
            );

            var featureColors = configuration.FeatureWindow.Colors;
            FeatureBorderColorTextBox.Text = featureColors.Border;
            FeatureAccentColorTextBox.Text = featureColors.Accent;
            FeatureBackgroundColorTextBox.Text = featureColors.Background;
            FeatureBackgroundOpacityTextBox.Text = featureColors.BackgroundOpacity.ToString(
                CultureInfo.InvariantCulture
            );
            FeatureTextColorTextBox.Text = featureColors.Text;

            FirstItemNumberComboBox.SelectedItem = GetOrCreateFirstItemKeyChoice(
                configuration.FeatureWindow.Hotkeys.FirstItemVirtualKey
            );
        });
    }

    private void ApplyHotkeysToControls(LuvLetterConfiguration configuration)
    {
        var inputHotkeys = configuration.InputBox.Hotkeys;
        SubmitHotkeyTextBox.Text = inputHotkeys.Submit.DisplayText;
        CancelHotkeyTextBox.Text = inputHotkeys.Cancel.DisplayText;
        BackspaceHotkeyTextBox.Text = inputHotkeys.Backspace.DisplayText;

        var featureHotkeys = configuration.FeatureWindow.Hotkeys;
        FeaturePreviousPageHotkeyTextBox.Text = featureHotkeys.PreviousPage.DisplayText;
        FeatureNextPageHotkeyTextBox.Text = featureHotkeys.NextPage.DisplayText;
        FeatureCancelHotkeyTextBox.Text = featureHotkeys.Cancel.DisplayText;
    }

    private void RefreshHotkeyControls()
    {
        RunWithoutPendingNotification(() => ApplyHotkeysToControls(pendingConfiguration));
    }

    private void RunWithoutPendingNotification(Action action)
    {
        var wasApplyingConfiguration = isApplyingConfiguration;
        isApplyingConfiguration = true;
        try
        {
            action();
        }
        finally
        {
            isApplyingConfiguration = wasApplyingConfiguration;
        }
    }

    private FirstItemKeyChoice GetOrCreateFirstItemKeyChoice(int virtualKey)
    {
        var choice = firstItemKeyChoices.FirstOrDefault(item => item.VirtualKey == virtualKey);
        if (choice is not null)
        {
            return choice;
        }

        choice = new FirstItemKeyChoice(virtualKey, $"VK 0x{virtualKey:X2}");
        firstItemKeyChoices.Add(choice);
        return choice;
    }

    private bool TryReadConfigurationFromControls(
        out LuvLetterConfiguration configuration,
        out string error
    )
    {
        configuration = pendingConfiguration;

        if (
            !TryReadInputBoxConfiguration(out var inputBox, out error)
            || !TryReadActivationGestures(out var activationGestures, out error)
            || !TryReadFeatureWindowConfiguration(out var featureWindow, out error)
        )
        {
            return false;
        }

        configuration = pendingConfiguration with
        {
            InputBox = inputBox,
            ActivationGestures = activationGestures,
            FeatureWindow = featureWindow,
        };
        return true;
    }

    private bool TryReadInputBoxConfiguration(
        out InputBoxConfiguration inputBox,
        out string error
    )
    {
        inputBox = pendingConfiguration.InputBox;
        error = string.Empty;

        if (PositionModeComboBox.SelectedItem is not InputBoxPositionMode positionMode)
        {
            error = "Select a command input position mode.";
            return false;
        }

        if (
            !TryReadInt(OffsetXTextBox, "Command input Offset X", out var offsetX, out error)
            || !TryReadInt(OffsetYTextBox, "Command input Offset Y", out var offsetY, out error)
            || !TryReadInt(
                BottomMarginTextBox,
                "Command input bottom margin",
                out var bottomMargin,
                out error
            )
            || !TryReadInt(CustomXTextBox, "Command input Custom X", out var customX, out error)
            || !TryReadInt(CustomYTextBox, "Command input Custom Y", out var customY, out error)
            || !TryReadInt(WidthTextBox, "Command input width", out var width, out error)
            || !TryReadInt(HeightTextBox, "Command input height", out var height, out error)
            || !TryReadFloat(FontSizeTextBox, "Command input font size", out var fontSize, out error)
            || !TryReadFloat(
                CornerRadiusTextBox,
                "Command input corner radius",
                out var cornerRadius,
                out error
            )
            || !TryReadFloat(
                BorderThicknessTextBox,
                "Command input border thickness",
                out var borderThickness,
                out error
            )
            || !TryReadFloat(
                HorizontalPaddingTextBox,
                "Command input horizontal padding",
                out var horizontalPadding,
                out error
            )
            || !TryReadFloat(
                VerticalPaddingTextBox,
                "Command input vertical padding",
                out var verticalPadding,
                out error
            )
            || !TryReadFloat(
                CaretWidthTextBox,
                "Command input caret width",
                out var caretWidth,
                out error
            )
            || !TryReadOpacity(
                BackgroundOpacityTextBox,
                "Command input background opacity",
                out var backgroundOpacity,
                out error
            )
            || !TryReadColor(
                BorderColorTextBox,
                "Command input border color",
                out var borderColor,
                out error
            )
            || !TryReadColor(
                BackgroundColorTextBox,
                "Command input background color",
                out var backgroundColor,
                out error
            )
            || !TryReadColor(
                TextColorTextBox,
                "Command input text color",
                out var textColor,
                out error
            )
            || !TryReadColor(
                CaretColorTextBox,
                "Command input caret color",
                out var caretColor,
                out error
            )
        )
        {
            return false;
        }

        inputBox = pendingConfiguration.InputBox with
        {
            Placement = pendingConfiguration.InputBox.Placement with
            {
                Mode = positionMode,
                OffsetX = offsetX,
                OffsetY = offsetY,
                BottomMargin = bottomMargin,
                CustomX = customX,
                CustomY = customY,
            },
            Colors = pendingConfiguration.InputBox.Colors with
            {
                Border = borderColor,
                Background = ApplyOpacityToColor(backgroundColor, backgroundOpacity),
                BackgroundOpacity = backgroundOpacity,
                Text = textColor,
                Caret = caretColor,
            },
            Size = pendingConfiguration.InputBox.Size with
            {
                Width = width,
                Height = height,
                FontSize = fontSize,
                CornerRadius = cornerRadius,
                BorderThickness = borderThickness,
                HorizontalPadding = horizontalPadding,
                VerticalPadding = verticalPadding,
                CaretWidth = caretWidth,
            },
        };
        return true;
    }

    private bool TryReadActivationGestures(
        out ActivationGestureOptions gestures,
        out string error
    )
    {
        gestures = pendingConfiguration.ActivationGestures;
        error = string.Empty;

        if (InputBoxGestureComboBox.SelectedItem is not GestureChoice inputBoxGestureChoice)
        {
            error = "Select a command input activation gesture.";
            return false;
        }

        if (
            FeatureWindowGestureComboBox.SelectedItem
            is not GestureChoice featureWindowGestureChoice
        )
        {
            error = "Select a feature window activation gesture.";
            return false;
        }

        if (inputBoxGestureChoice.Kind == featureWindowGestureChoice.Kind)
        {
            error = "Command input and feature window must use different gestures.";
            return false;
        }

        var allowLeftControl = AllowLeftControlCheckBox.IsChecked == true;
        var allowRightControl = AllowRightControlCheckBox.IsChecked == true;
        if (!allowLeftControl && !allowRightControl)
        {
            error = "Enable at least one Ctrl key.";
            return false;
        }

        if (
            !TryReadInt(
                TapMaxDurationTextBox,
                "Tap maximum duration",
                out var tapMaxDuration,
                out error
            )
            || !TryReadInt(
                SecondPressTimeoutTextBox,
                "Second press timeout",
                out var secondPressTimeout,
                out error
            )
            || !TryReadInt(
                HoldThresholdTextBox,
                "Hold threshold",
                out var holdThreshold,
                out error
            )
        )
        {
            return false;
        }

        if (tapMaxDuration <= 0 || secondPressTimeout <= 0 || holdThreshold <= 0)
        {
            error = "Gesture timings must be greater than zero milliseconds.";
            return false;
        }

        gestures = pendingConfiguration.ActivationGestures with
        {
            InputBox = inputBoxGestureChoice.Kind,
            FeatureWindow = featureWindowGestureChoice.Kind,
            TapMaxDurationMs = tapMaxDuration,
            SecondPressTimeoutMs = secondPressTimeout,
            HoldThresholdMs = holdThreshold,
            AllowLeftControl = allowLeftControl,
            AllowRightControl = allowRightControl,
        };
        return true;
    }

    private bool TryReadFeatureWindowConfiguration(
        out FeatureWindowConfiguration featureWindow,
        out string error
    )
    {
        featureWindow = pendingConfiguration.FeatureWindow;
        error = string.Empty;

        if (
            FirstItemNumberComboBox.SelectedItem is not FirstItemKeyChoice firstItemKey
        )
        {
            error = "Select the first feature item number key.";
            return false;
        }

        if (
            !TryReadInt(
                FeatureItemsPerPageTextBox,
                "Feature items per page",
                out var itemsPerPage,
                out error
            )
            || !TryReadInt(
                FeatureCellSizeTextBox,
                "Feature cell size",
                out var cellSize,
                out error
            )
            || !TryReadInt(FeatureGapTextBox, "Feature gap", out var gap, out error)
            || !TryReadFloat(
                FeatureCornerRadiusTextBox,
                "Feature corner radius",
                out var cornerRadius,
                out error
            )
            || !TryReadFloat(
                FeatureBorderThicknessTextBox,
                "Feature border thickness",
                out var borderThickness,
                out error
            )
            || !TryReadFloat(
                FeatureFontSizeTextBox,
                "Feature font size",
                out var fontSize,
                out error
            )
            || !TryReadInt(
                FeatureBottomMarginTextBox,
                "Feature bottom margin",
                out var bottomMargin,
                out error
            )
            || !TryReadInt(
                FeatureOffsetXTextBox,
                "Feature Offset X",
                out var offsetX,
                out error
            )
            || !TryReadInt(
                FeatureOffsetYTextBox,
                "Feature Offset Y",
                out var offsetY,
                out error
            )
            || !TryReadOpacity(
                FeatureBackgroundOpacityTextBox,
                "Feature background opacity",
                out var backgroundOpacity,
                out error
            )
            || !TryReadColor(
                FeatureBorderColorTextBox,
                "Feature border color",
                out var borderColor,
                out error
            )
            || !TryReadColor(
                FeatureAccentColorTextBox,
                "Feature accent color",
                out var accentColor,
                out error
            )
            || !TryReadColor(
                FeatureBackgroundColorTextBox,
                "Feature background color",
                out var backgroundColor,
                out error
            )
            || !TryReadColor(
                FeatureTextColorTextBox,
                "Feature text color",
                out var textColor,
                out error
            )
        )
        {
            return false;
        }

        if (itemsPerPage is < 1 or > FeatureWindowLayoutOptions.MaximumItemsPerPage)
        {
            error = $"Feature items per page must be between 1 and {FeatureWindowLayoutOptions.MaximumItemsPerPage}.";
            return false;
        }

        if (
            firstItemKey.VirtualKey is >= 0x30 and <= 0x39
            && firstItemKey.VirtualKey + itemsPerPage - 1 > 0x39
        )
        {
            error = "The first item number and items per page must stay within keys 0-9.";
            return false;
        }

        var featureHotkeys = pendingConfiguration.FeatureWindow.Hotkeys;
        if (HotkeyEquals(featureHotkeys.PreviousPage, featureHotkeys.NextPage)
            || HotkeyEquals(featureHotkeys.PreviousPage, featureHotkeys.Cancel)
            || HotkeyEquals(featureHotkeys.NextPage, featureHotkeys.Cancel))
        {
            error = "Feature previous, next, and cancel hotkeys must be different.";
            return false;
        }

        if (ConflictsWithFeatureNumbers(featureHotkeys.PreviousPage, firstItemKey.VirtualKey, itemsPerPage)
            || ConflictsWithFeatureNumbers(featureHotkeys.NextPage, firstItemKey.VirtualKey, itemsPerPage)
            || ConflictsWithFeatureNumbers(featureHotkeys.Cancel, firstItemKey.VirtualKey, itemsPerPage))
        {
            error = "Feature navigation and cancel hotkeys cannot overlap the item number keys.";
            return false;
        }

        featureWindow = pendingConfiguration.FeatureWindow with
        {
            Layout = pendingConfiguration.FeatureWindow.Layout with
            {
                ItemsPerPage = itemsPerPage,
                CellSize = cellSize,
                Gap = gap,
                CornerRadius = cornerRadius,
                BorderThickness = borderThickness,
                FontSize = fontSize,
                BottomMargin = bottomMargin,
                OffsetX = offsetX,
                OffsetY = offsetY,
            },
            Colors = pendingConfiguration.FeatureWindow.Colors with
            {
                Border = borderColor,
                Background = ApplyOpacityToColor(backgroundColor, backgroundOpacity),
                BackgroundOpacity = backgroundOpacity,
                Text = textColor,
                Accent = accentColor,
            },
            Hotkeys = pendingConfiguration.FeatureWindow.Hotkeys with
            {
                FirstItemVirtualKey = firstItemKey.VirtualKey,
            },
        };
        return true;
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

        error = $"{label} must be an integer.";
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
            && float.IsFinite(value)
        )
        {
            error = string.Empty;
            return true;
        }

        error = $"{label} must be a finite number.";
        return false;
    }

    private static bool TryReadOpacity(
        WpfTextBox textBox,
        string label,
        out float value,
        out string error
    )
    {
        if (!TryReadFloat(textBox, label, out value, out error))
        {
            return false;
        }

        if (value is < 0.0f or > 1.0f)
        {
            error = $"{label} must be between 0 and 1.";
            return false;
        }

        return true;
    }

    private static bool TryReadColor(
        WpfTextBox textBox,
        string label,
        out string value,
        out string error
    )
    {
        value = textBox.Text.Trim();
        var hex = value.TrimStart('#');
        if (
            hex.Length is 6 or 8
            && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
        )
        {
            error = string.Empty;
            return true;
        }

        error = $"{label} must be #RRGGBB or #AARRGGBB.";
        return false;
    }

    private static string ApplyOpacityToColor(string value, float opacity)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        var alpha = (int)Math.Round(Math.Clamp(opacity, 0.0f, 1.0f) * 255.0f);
        return $"#{alpha:X2}{hex[^6..]}";
    }

    private static bool TryCreateHotkey(
        WpfKeyEventArgs eventArgs,
        out HotkeyDefinition hotkey,
        out string error
    )
    {
        hotkey = HotkeyDefinition.Default;
        error = string.Empty;

        var key = GetEventKey(eventArgs);
        if (IsModifierKey(key))
        {
            error = "Press a non-modifier key.";
            return false;
        }

        var modifiers = GetCurrentModifiers();
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            error = "This key is not supported.";
            return false;
        }

        hotkey = new HotkeyDefinition(modifiers, virtualKey, NormalizeKeyName(key));
        return true;
    }

    private static Key GetEventKey(WpfKeyEventArgs eventArgs)
    {
        var key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        return key == Key.ImeProcessed ? eventArgs.ImeProcessedKey : key;
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

    private static bool IsModifierKey(Key key) => key is
        Key.LeftAlt
        or Key.RightAlt
        or Key.LeftCtrl
        or Key.RightCtrl
        or Key.LeftShift
        or Key.RightShift
        or Key.LWin
        or Key.RWin
        or Key.System;

    private static string NormalizeKeyName(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return $"NumPad{(int)(key - Key.NumPad0)}";
        }

        return key switch
        {
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Space => "Space",
            _ => key.ToString(),
        };
    }

    private sealed record GestureChoice(ActivationGestureKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record FirstItemKeyChoice(int VirtualKey, string Label)
    {
        public override string ToString() => Label;
    }
}
