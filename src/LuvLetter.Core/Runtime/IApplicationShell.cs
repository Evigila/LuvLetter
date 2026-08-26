namespace LuvLetter.Core.Runtime;

/// <summary>
/// UI capability consumed by the Core runtime. Implementations own dispatcher
/// marshalling and view lifetime; Core never references WPF types.
/// </summary>
public interface IApplicationShell
{
    void StartMinimized();

    void ShowSettings();

    void ReportStatus(string message);
}
