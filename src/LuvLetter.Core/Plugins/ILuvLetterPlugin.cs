namespace LuvLetter.Core.Plugins;

public interface ILuvLetterPlugin
{
    string Id { get; }

    void Register(PluginRegistrationContext context);
}
