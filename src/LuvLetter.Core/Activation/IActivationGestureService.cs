using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.Activation;

public interface IActivationGestureService
{
    event EventHandler? CommandInputRequested;

    event EventHandler? PopupsDismissRequested;

    event EventHandler? QuickActionsRequested;

    void Start(ActivationGestureOptions options);

    void Update(ActivationGestureOptions options);

    void CancelPendingGestures();

    void Stop();
}
