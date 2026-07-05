namespace LuvLetter.Core.Native;

public sealed class InputBoxService : IInputBoxService
{
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
}
