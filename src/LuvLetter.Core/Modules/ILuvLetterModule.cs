namespace LuvLetter.Core.Modules;

public interface ILuvLetterModule
{
    string Id { get; }

    void Register(ModuleRegistrationContext context);
}
