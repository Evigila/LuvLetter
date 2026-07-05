namespace LuvLetter.Core.Native;

public interface IInputBoxService : IDisposable
{
    void Show();
    void Hide();
    void Toggle();
}
