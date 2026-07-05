using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.Native;

public interface IInputBoxService : IDisposable
{
    void ApplyConfiguration(InputBoxConfiguration configuration);
    void Show();
    void Hide();
    void Toggle();
}
