namespace LuvLetter.Core.Application;

/// <summary>
/// Extension boundary for non-command inputs accepted by General mode. The
/// future file-index feature can implement this interface without coupling the
/// application coordinator to its index or platform implementation.
/// </summary>
public interface IGeneralInputMatcher
{
    bool TryHandle(string input);
}
