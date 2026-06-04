namespace LuvLetter.Commands;

public sealed record CommandMetadata(
    string Name,
    string Description,
    CommandExecutionTarget DefaultTarget,
    IReadOnlyList<string> Aliases)
{
    public IEnumerable<string> GetRouteKeys()
    {
        yield return Name;

        foreach (var alias in Aliases)
        {
            yield return alias;
        }
    }
}
