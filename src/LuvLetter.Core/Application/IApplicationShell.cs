namespace LuvLetter.Core.Application;

/// <summary>
/// UI capability consumed by the application coordinator. Implementations own
/// dispatcher marshalling and view lifetime; Core never references WPF types.
/// </summary>
public interface IApplicationShell
{
    void ShowSettings();

    void ReportStatus(string message);
}
