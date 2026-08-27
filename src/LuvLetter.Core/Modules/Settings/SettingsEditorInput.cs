using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.Modules.Settings;

public sealed record SettingsEditorInput(
    InputBoxSettingsInput InputBox,
    ActivationGestureSettingsInput ActivationGestures,
    QuickActionsSettingsInput QuickActions
);

public sealed record InputBoxSettingsInput(
    InputBoxPositionMode? PositionMode,
    string OffsetX,
    string OffsetY,
    string BottomMargin,
    string CustomX,
    string CustomY,
    string BorderColor,
    string BackgroundColor,
    string BackgroundOpacity,
    string TextColor,
    string TextOpacity,
    string CaretColor,
    string Width,
    string Height,
    string FontSize,
    string CornerRadius,
    string BorderThickness,
    string HorizontalPadding,
    string VerticalPadding,
    string CaretWidth
);

public sealed record ActivationGestureSettingsInput(
    string TapMaxDuration,
    string SecondPressTimeout,
    bool AllowLeftControl,
    bool AllowRightControl
);

public sealed record QuickActionsSettingsInput(
    int? FirstItemVirtualKey,
    string ItemsPerPage,
    string CellSize,
    string Gap,
    string CornerRadius,
    string BorderThickness,
    string FontSize,
    string BottomMargin,
    string OffsetX,
    string OffsetY,
    string BorderColor,
    string AccentColor,
    string BackgroundColor,
    string BackgroundOpacity,
    string TextColor,
    string TextOpacity
);
