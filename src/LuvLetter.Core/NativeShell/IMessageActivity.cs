namespace LuvLetter.Core.NativeShell;

/// <summary>
/// Owns one persistent message-queue bubble for a long-running operation.
/// </summary>
public interface IMessageActivity : IDisposable
{
    void Update(string message);

    /// <summary>
    /// Ends the activity. A non-empty final message remains as an ordinary transient bubble.
    /// </summary>
    void Complete(string? finalMessage = null);
}
