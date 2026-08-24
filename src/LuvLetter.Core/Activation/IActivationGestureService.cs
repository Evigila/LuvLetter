using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.Activation;

public interface IActivationGestureService
{
    void Update(ActivationGestureOptions options);

    void CancelPendingGestures();
}
