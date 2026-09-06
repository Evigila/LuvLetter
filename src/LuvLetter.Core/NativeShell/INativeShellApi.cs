namespace LuvLetter.Core.NativeShell;

internal interface INativeShellApi
{
    uint AbiVersion { get; }

    void EnsureCompatible();

    int ApplyInputBoxConfig(in NativeInputBoxConfig config);

    int SetInputSubmittedCallback(NativeInputSubmittedCallback? callback, IntPtr context);

    int SetInputChangedCallback(NativeInputChangedCallback? callback, IntPtr context);

    int SetCandidateActivatedCallback(NativeCandidateActivatedCallback? callback, IntPtr context);

    int SetInputCandidates(NativeInputCandidate[] items, int count, ulong revision);

    int ReplaceInputBoxText(string text, int length, int inputMode);

    int ShowInputBox();

    int HideInputBox();

    int DismissInputBox();

    int ToggleInputBox();

    int ApplyFeatureWindowConfig(in NativeFeatureWindowConfig config);

    int SetFeatureItems(NativeFeatureItem[] items, int count);

    int SetFeatureActivatedCallback(NativeFeatureActivatedCallback? callback, IntPtr context);

    int ShowFeatureWindow();

    int HideFeatureWindow();

    int ToggleFeatureWindow();

    int EnqueueMessage(string text, int length);

    int BeginMessageActivity(ulong token, string text, int length);

    int UpdateMessageActivity(ulong token, string text, int length);

    int CompleteMessageActivity(ulong token, string? finalText, int length);

    int ToggleMessageQueue();

    int HideMessageQueue();

    int HidePopups();

    int ShutdownInputBox();
}
