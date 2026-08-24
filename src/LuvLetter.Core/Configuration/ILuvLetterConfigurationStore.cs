namespace LuvLetter.Core.Configuration;

public interface ILuvLetterConfigurationStore
{
    LuvLetterConfiguration Current { get; }

    LuvLetterConfiguration Update(LuvLetterConfiguration configuration);
}
