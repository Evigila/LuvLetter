using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LuvLetter.Core.Configuration;
using LuvLetter.Core.Modules.Settings;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LuvLetter.View.Settings;

public partial class SettingsWindow : Window
{
    private readonly ISettingsService settingsService;
    private readonly ObservableCollection<SettingsChoice<int>> firstItemKeyChoices =
        new(
            Enumerable.Range(0, 10).Select(
                digit => new SettingsChoice<int>(
                    0x30 + digit,
                    digit.ToString(CultureInfo.InvariantCulture))));
    private readonly IReadOnlyDictionary<WpfTextBox, SettingsHotkeyField> hotkeyFields;

    private LuvLetterConfiguration pendingConfiguration;
    private bool isApplyingConfiguration = true;

    private sealed record SettingsChoice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsWindow(ISettingsService settingsService)
    {
        this.settingsService = settingsService;
        pendingConfiguration = settingsService.Current;

        FontFamily = new WpfFontFamily(SurfaceStyleDefaults.FontFamily);
        FontSize = SurfaceStyleDefaults.FontSize;
        InitializeComponent();
        PositionModeComboBox.ItemsSource = Enum.GetValues<InputBoxPositionMode>();
        FirstItemNumberComboBox.ItemsSource = firstItemKeyChoices;
        hotkeyFields = new Dictionary<WpfTextBox, SettingsHotkeyField>
        {
            [SubmitHotkeyTextBox] = SettingsHotkeyField.InputSubmit,
            [CancelHotkeyTextBox] = SettingsHotkeyField.InputCancel,
            [BackspaceHotkeyTextBox] = SettingsHotkeyField.InputBackspace,
            [QuickActionsPreviousPageHotkeyTextBox] = SettingsHotkeyField.QuickActionsPreviousPage,
            [QuickActionsNextPageHotkeyTextBox] = SettingsHotkeyField.QuickActionsNextPage,
            [QuickActionsCancelHotkeyTextBox] = SettingsHotkeyField.QuickActionsCancel,
        };

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
        if (HotkeyCapture.GetEventKey(eventArgs) == Key.Tab)
        {
            // Keep normal Tab and Shift+Tab focus traversal in read-only hotkey fields.
            return;
        }

        eventArgs.Handled = true;
        if (sender is not WpfTextBox textBox || !hotkeyFields.TryGetValue(textBox, out var field))
        {
            SetStatus("Unknown hotkey field");
            return;
        }

        if (!HotkeyCapture.TryCreate(eventArgs, out var hotkey, out var error))
        {
            SetStatus(error);
            return;
        }

        pendingConfiguration = settingsService.ReplaceHotkey(
            pendingConfiguration,
            field,
            hotkey
        );
        RefreshHotkeyControls();
        SetStatus("Pending");
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        // Applying settings is an explicit interaction, not the completion of a
        // Ctrl gesture that happened while editing them. This also invalidates any
        // gesture action already queued on the dispatcher.
        settingsService.CancelPendingGestures();

        if (!TryReadConfigurationFromControls(out var rawConfiguration, out var error))
        {
            SetStatus(error);
            return;
        }

        var result = settingsService.Apply(rawConfiguration);
        if (result.DisplayConfiguration is { } displayConfiguration)
        {
            pendingConfiguration = displayConfiguration;
            ApplyConfigurationToControls(displayConfiguration);
        }

        SetStatus(result.Message);
    }

    private void ApplyButton_OnPreviewMouseDown(
        object sender,
        MouseButtonEventArgs eventArgs
    ) => settingsService.CancelPendingGestures();

    private void ResetButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        pendingConfiguration = settingsService.CreateDefaultConfiguration();
        ApplyConfigurationToControls(pendingConfiguration);
        SetStatus("Pending");
    }
}
