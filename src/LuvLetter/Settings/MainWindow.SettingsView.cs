using System.Globalization;
using LuvLetter.Core.Configuration;
using LuvLetter.Settings;

namespace LuvLetter;

public partial class MainWindow
{
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
            TextOpacityTextBox.Text = inputColors.TextOpacity.ToString(
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
            FeatureTextOpacityTextBox.Text = featureColors.TextOpacity.ToString(
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

    private void RefreshHotkeyControls() =>
        RunWithoutPendingNotification(() => ApplyHotkeysToControls(pendingConfiguration));

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
    ) => SettingsConfigurationParser.TryParse(
        pendingConfiguration,
        CaptureSettingsInput(),
        out configuration,
        out error
    );

    private SettingsEditorInput CaptureSettingsInput() =>
        new(
            new InputBoxSettingsInput(
                PositionModeComboBox.SelectedItem is InputBoxPositionMode positionMode
                    ? positionMode
                    : null,
                OffsetXTextBox.Text,
                OffsetYTextBox.Text,
                BottomMarginTextBox.Text,
                CustomXTextBox.Text,
                CustomYTextBox.Text,
                BorderColorTextBox.Text,
                BackgroundColorTextBox.Text,
                BackgroundOpacityTextBox.Text,
                TextColorTextBox.Text,
                TextOpacityTextBox.Text,
                CaretColorTextBox.Text,
                WidthTextBox.Text,
                HeightTextBox.Text,
                FontSizeTextBox.Text,
                CornerRadiusTextBox.Text,
                BorderThicknessTextBox.Text,
                HorizontalPaddingTextBox.Text,
                VerticalPaddingTextBox.Text,
                CaretWidthTextBox.Text
            ),
            new ActivationGestureSettingsInput(
                (InputBoxGestureComboBox.SelectedItem as GestureChoice)?.Kind,
                (FeatureWindowGestureComboBox.SelectedItem as GestureChoice)?.Kind,
                TapMaxDurationTextBox.Text,
                SecondPressTimeoutTextBox.Text,
                HoldThresholdTextBox.Text,
                AllowLeftControlCheckBox.IsChecked == true,
                AllowRightControlCheckBox.IsChecked == true
            ),
            new FeatureWindowSettingsInput(
                (FirstItemNumberComboBox.SelectedItem as FirstItemKeyChoice)?.VirtualKey,
                FeatureItemsPerPageTextBox.Text,
                FeatureCellSizeTextBox.Text,
                FeatureGapTextBox.Text,
                FeatureCornerRadiusTextBox.Text,
                FeatureBorderThicknessTextBox.Text,
                FeatureFontSizeTextBox.Text,
                FeatureBottomMarginTextBox.Text,
                FeatureOffsetXTextBox.Text,
                FeatureOffsetYTextBox.Text,
                FeatureBorderColorTextBox.Text,
                FeatureAccentColorTextBox.Text,
                FeatureBackgroundColorTextBox.Text,
                FeatureBackgroundOpacityTextBox.Text,
                FeatureTextColorTextBox.Text,
                FeatureTextOpacityTextBox.Text
            )
        );
}
