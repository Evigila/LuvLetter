using LuvLetter.Core.Configuration;

namespace LuvLetter.Settings;

internal sealed record SettingsEditorInput(
    InputBoxSettingsInput InputBox,
    ActivationGestureSettingsInput ActivationGestures,
    FeatureWindowSettingsInput FeatureWindow
);

internal sealed record InputBoxSettingsInput(
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

internal sealed record ActivationGestureSettingsInput(
    ActivationGestureKind? InputBoxGesture,
    ActivationGestureKind? FeatureWindowGesture,
    string TapMaxDuration,
    string SecondPressTimeout,
    string HoldThreshold,
    bool AllowLeftControl,
    bool AllowRightControl
);

internal sealed record FeatureWindowSettingsInput(
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
