using LuvLetter.Core.Configuration;

namespace LuvLetter.Core.Application;

public sealed record ConfigurationApplicationResult(
    bool Succeeded,
    string Message,
    LuvLetterConfiguration? DisplayConfiguration);
