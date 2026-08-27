using System.Globalization;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.Settings;

namespace LuvLetter.View.Settings;

public partial class SettingsWindow
{
    private void ApplyConfigurationToControls(LuvLetterConfiguration configuration)
    {
        RunWithoutPendingNotification(() =>
        {
            ApplyHotkeysToControls(configuration);

            var gestures = configuration.ActivationGestures;
            TapMaxDurationTextBox.Text = gestures.TapMaxDurationMs.ToString(
                CultureInfo.InvariantCulture
            );
            SecondPressTimeoutTextBox.Text = gestures.SecondPressTimeoutMs.ToString(
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

            var quickActionsLayout = configuration.QuickActions.Layout;
            QuickActionsItemsPerPageTextBox.Text = quickActionsLayout.ItemsPerPage.ToString(
                CultureInfo.InvariantCulture
            );
            QuickActionsCellSizeTextBox.Text = quickActionsLayout.CellSize.ToString(
                CultureInfo.InvariantCulture
            );
            QuickActionsGapTextBox.Text = quickActionsLayout.Gap.ToString(CultureInfo.InvariantCulture);
            QuickActionsCornerRadiusTextBox.Text = quickActionsLayout.CornerRadius.ToString(
                CultureInfo.InvariantCulture
            );
            QuickActionsBorderThicknessTextBox.Text = quickActionsLayout.BorderThickness.ToString(
                CultureInfo.InvariantCulture
            );
            QuickActionsFontSizeTextBox.Text = quickActionsLayout.FontSize.ToString(
                CultureInfo.InvariantCulture
            );
            QuickActionsBottomMarginTextBox.Text = quickActionsLayout.BottomMargin.ToString(
                CultureInfo.InvariantCulture
            );
            QuickActionsOffsetXTextBox.Text = quickActionsLayout.OffsetX.ToString(
                CultureInfo.InvariantCulture
            );
            QuickActionsOffsetYTextBox.Text = quickActionsLayout.OffsetY.ToString(
                CultureInfo.InvariantCulture
            );

            var quickActionsColors = configuration.QuickActions.Colors;
            QuickActionsBorderColorTextBox.Text = quickActionsColors.Border;
            QuickActionsAccentColorTextBox.Text = quickActionsColors.Accent;
            QuickActionsBackgroundColorTextBox.Text = quickActionsColors.Background;
            QuickActionsBackgroundOpacityTextBox.Text = quickActionsColors.BackgroundOpacity.ToString(
                CultureInfo.InvariantCulture
            );
            QuickActionsTextOpacityTextBox.Text = quickActionsColors.TextOpacity.ToString(
                CultureInfo.InvariantCulture
            );
            QuickActionsTextColorTextBox.Text = quickActionsColors.Text;

            FirstItemNumberComboBox.SelectedItem = GetOrCreateFirstItemKeyChoice(
                configuration.QuickActions.Hotkeys.FirstItemVirtualKey
            );
        });
    }

    private void ApplyHotkeysToControls(LuvLetterConfiguration configuration)
    {
        var inputHotkeys = configuration.InputBox.Hotkeys;
        SubmitHotkeyTextBox.Text = HotkeyCapture.Format(inputHotkeys.Submit);
        CancelHotkeyTextBox.Text = HotkeyCapture.Format(inputHotkeys.Cancel);
        BackspaceHotkeyTextBox.Text = HotkeyCapture.Format(inputHotkeys.Backspace);

        var quickActionsHotkeys = configuration.QuickActions.Hotkeys;
        QuickActionsPreviousPageHotkeyTextBox.Text = HotkeyCapture.Format(quickActionsHotkeys.PreviousPage);
        QuickActionsNextPageHotkeyTextBox.Text = HotkeyCapture.Format(quickActionsHotkeys.NextPage);
        QuickActionsCancelHotkeyTextBox.Text = HotkeyCapture.Format(quickActionsHotkeys.Cancel);
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

    private SettingsChoice<int> GetOrCreateFirstItemKeyChoice(int virtualKey)
    {
        var choice = firstItemKeyChoices.FirstOrDefault(item => item.Value == virtualKey);
        if (choice is not null)
        {
            return choice;
        }

        choice = new SettingsChoice<int>(virtualKey, $"VK 0x{virtualKey:X2}");
        firstItemKeyChoices.Add(choice);
        return choice;
    }

    private bool TryReadConfigurationFromControls(
        out LuvLetterConfiguration configuration,
        out string error
    ) => settingsService.TryMap(
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
                TapMaxDurationTextBox.Text,
                SecondPressTimeoutTextBox.Text,
                AllowLeftControlCheckBox.IsChecked == true,
                AllowRightControlCheckBox.IsChecked == true
            ),
            new QuickActionsSettingsInput(
                (FirstItemNumberComboBox.SelectedItem as SettingsChoice<int>)?.Value,
                QuickActionsItemsPerPageTextBox.Text,
                QuickActionsCellSizeTextBox.Text,
                QuickActionsGapTextBox.Text,
                QuickActionsCornerRadiusTextBox.Text,
                QuickActionsBorderThicknessTextBox.Text,
                QuickActionsFontSizeTextBox.Text,
                QuickActionsBottomMarginTextBox.Text,
                QuickActionsOffsetXTextBox.Text,
                QuickActionsOffsetYTextBox.Text,
                QuickActionsBorderColorTextBox.Text,
                QuickActionsAccentColorTextBox.Text,
                QuickActionsBackgroundColorTextBox.Text,
                QuickActionsBackgroundOpacityTextBox.Text,
                QuickActionsTextColorTextBox.Text,
                QuickActionsTextOpacityTextBox.Text
            )
        );
}
