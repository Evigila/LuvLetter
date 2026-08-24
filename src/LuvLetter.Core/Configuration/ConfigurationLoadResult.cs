namespace LuvLetter.Core.Configuration;

public enum ConfigurationLoadStatus
{
    Loaded,
    NotFound,
    Invalid,
    UnsupportedVersion,
    IoFailure,
}

public sealed record ConfigurationLoadResult(
    LuvLetterConfiguration Configuration,
    ConfigurationLoadStatus Status,
    string? Message = null,
    Exception? Exception = null)
{
    public bool HasWarning => Status is
        ConfigurationLoadStatus.Invalid
        or ConfigurationLoadStatus.UnsupportedVersion
        or ConfigurationLoadStatus.IoFailure;
}
