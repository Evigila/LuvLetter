using LuvLetter.Core.Concurrency;
using ArkheideSystem;

namespace LuvLetter.Core.Commands;

public enum CommandRegistrationMode
{
    RejectDuplicate,
    ReplaceExisting,
}

public enum CommandDispatchResult
{
    Accepted,
    RejectedEmpty,
    RejectedIncomplete,
    QueueFull,
    Disposed,
}

public enum CommandRouteKind
{
    Domain,
    Command,
    Alias,
    Link,
    Option,
}

public sealed record CommandInvocation(
    string OriginalText,
    string Text,
    string Domain,
    string InvokedPath,
    string CommandPath,
    string Arguments)
{
    public string QualifiedName => $"{Domain} {CommandPath}";

    public string InvokedQualifiedName => $"{Domain} {InvokedPath}";
}

public sealed record CommandSuggestion(
    string Label,
    string CompletionText,
    string ExecutionText,
    bool CanExecute,
    CommandRouteKind Kind,
    string? TargetPath,
    string Description = "");

/// <summary>
/// A bounded, single-consumer command dispatcher. Registered routes form a command tree;
/// aliases share definitions while links rewrite a source path prefix before resolution.
/// </summary>
public sealed class CommandDispatcher : ICommandRegistrar, IDisposable
{
    private const int DefaultCapacity = 64;
    private const int MaximumLinkHops = 32;

    private readonly object routesLock = new();
    private readonly Dictionary<string, CommandRoute> routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly BoundedSerialQueue<string> dispatchQueue;

    public CommandDispatcher(int capacity = DefaultCapacity)
    {
        dispatchQueue = new(capacity, Process);
    }

    public event Action<CommandInvocation>? Unhandled;

    public event Action<CommandInvocation, Exception>? Failed;

    public bool Register(
        string commandDomain,
        string commandPath,
        Action<CommandInvocation> handler,
        CommandRegistrationMode mode = CommandRegistrationMode.RejectDuplicate,
        IReadOnlyList<CommandOption>? options = null)
    {
        var domain = NormalizeDomain(commandDomain, nameof(commandDomain));
        var path = NormalizePath(commandPath, nameof(commandPath));
        ArgumentNullException.ThrowIfNull(handler);
        ValidateMode(mode);
        var registeredOptions = options?.ToArray() ?? [];
        if (registeredOptions.Any(static option => option is null))
        {
            throw new ArgumentException("Command options cannot contain null entries.", nameof(options));
        }
        var optionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (registeredOptions.SelectMany(static option => option.Names).Any(name => !optionNames.Add(name)))
        {
            throw new ArgumentException("Command option names must be unique.", nameof(options));
        }

        lock (routesLock)
        {
            var key = RouteKey(domain, path);
            if (routes.TryGetValue(key, out var existing))
            {
                if (mode == CommandRegistrationMode.RejectDuplicate)
                {
                    return false;
                }

                if (existing.Kind == CommandRouteKind.Command && existing.Definition is not null)
                {
                    existing.Definition.Handler = handler;
                    existing.Definition.Options = registeredOptions;
                    return true;
                }
            }

            var definition = new CommandDefinition(domain, path, handler, registeredOptions);
            routes[key] = CommandRoute.Command(domain, path, definition);
            return true;
        }
    }

    public bool RegisterAlias(
        string aliasDomain,
        string aliasPath,
        string targetDomain,
        string targetPath)
    {
        var source = NormalizeAddress(aliasDomain, aliasPath, nameof(aliasDomain), nameof(aliasPath));
        var target = NormalizeAddress(targetDomain, targetPath, nameof(targetDomain), nameof(targetPath));
        lock (routesLock)
        {
            var sourceKey = RouteKey(source.Domain, source.Path);
            if (routes.ContainsKey(sourceKey)
                || !routes.TryGetValue(RouteKey(target.Domain, target.Path), out var targetRoute)
                || targetRoute.Definition is null)
            {
                return false;
            }

            routes.Add(
                sourceKey,
                CommandRoute.Alias(source.Domain, source.Path, targetRoute.Definition));
            return true;
        }
    }

    public bool RegisterLink(
        string sourceDomain,
        string sourcePath,
        string targetDomain,
        string targetPath)
    {
        var source = NormalizeAddress(sourceDomain, sourcePath, nameof(sourceDomain), nameof(sourcePath));
        var target = NormalizeAddress(targetDomain, targetPath, nameof(targetDomain), nameof(targetPath));
        lock (routesLock)
        {
            var sourceKey = RouteKey(source.Domain, source.Path);
            if (routes.ContainsKey(sourceKey)
                || HasDescendantLocked(source)
                || HasLinkAncestorLocked(source)
                || !HasPathLocked(target))
            {
                return false;
            }

            var link = CommandRoute.Link(source.Domain, source.Path, target);
            routes.Add(sourceKey, link);
            if (!TryRewriteLinksLocked($"{source.Domain} {source.Path}", out _, out _))
            {
                routes.Remove(sourceKey);
                return false;
            }

            return true;
        }
    }

    public bool Unregister(string commandDomain, string commandPath)
    {
        var domain = NormalizeDomain(commandDomain, nameof(commandDomain));
        var path = NormalizePath(commandPath, nameof(commandPath));
        lock (routesLock)
        {
            var key = RouteKey(domain, path);
            if (!routes.Remove(key, out var removed))
            {
                return false;
            }

            if (removed.Kind == CommandRouteKind.Command && removed.Definition is not null)
            {
                foreach (var aliasKey in routes
                    .Where(pair => ReferenceEquals(pair.Value.Definition, removed.Definition))
                    .Select(static pair => pair.Key)
                    .ToArray())
                {
                    routes.Remove(aliasKey);
                }
            }
            return true;
        }
    }

    public bool IsRegistered(string commandDomain, string commandPath)
    {
        var address = NormalizeAddress(
            commandDomain,
            commandPath,
            nameof(commandDomain),
            nameof(commandPath));
        lock (routesLock)
        {
            return routes.ContainsKey(RouteKey(address.Domain, address.Path));
        }
    }

    public bool IsExecutable(string commandDomain, string commandPath)
    {
        var address = NormalizeAddress(
            commandDomain,
            commandPath,
            nameof(commandDomain),
            nameof(commandPath));
        lock (routesLock)
        {
            return TryResolveDefinitionLocked($"{address.Domain} {address.Path}", out _, out _);
        }
    }

    public bool HasPath(string commandDomain, string commandPath)
    {
        var address = NormalizeAddress(
            commandDomain,
            commandPath,
            nameof(commandDomain),
            nameof(commandPath));
        lock (routesLock)
        {
            return HasPathLocked(address);
        }
    }

    public bool IsRegisteredDomainInvocation(string commandText)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        var domain = FirstToken(commandText);
        if (domain.Length == 0)
        {
            return false;
        }

        lock (routesLock)
        {
            return routes.Values.Any(
                route => string.Equals(route.Domain, domain, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool IsRegisteredInvocation(string commandText)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        lock (routesLock)
        {
            return TryResolveDefinitionLocked(commandText, out _, out _);
        }
    }

    public IReadOnlyList<string> RegisteredDomainsSnapshot()
    {
        lock (routesLock)
        {
            return routes.Values
                .Select(static route => route.Domain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(static domain => domain, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<string> RegisteredPathsSnapshot(string commandDomain)
    {
        var domain = NormalizeDomain(commandDomain, nameof(commandDomain));
        lock (routesLock)
        {
            return routes.Values
                .Where(route => string.Equals(route.Domain, domain, StringComparison.OrdinalIgnoreCase))
                .Select(static route => route.Path)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(static path => path, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<CommandSuggestion> Suggest(string commandText, int maximumResults)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        if (maximumResults <= 0)
        {
            return [];
        }

        lock (routesLock)
        {
            return SuggestLocked(commandText, maximumResults);
        }
    }

    public CommandDispatchResult Dispatch(string commandText)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        if (dispatchQueue.IsDisposed)
        {
            return CommandDispatchResult.Disposed;
        }

        var parsed = Parse(commandText);
        if (parsed.Tokens.Count == 0)
        {
            return CommandDispatchResult.RejectedEmpty;
        }
        if (parsed.Tokens.Count == 1)
        {
            return CommandDispatchResult.RejectedIncomplete;
        }

        if (!dispatchQueue.TryEnqueue(parsed.Text))
        {
            return dispatchQueue.IsDisposed
                ? CommandDispatchResult.Disposed
                : CommandDispatchResult.QueueFull;
        }

        return CommandDispatchResult.Accepted;
    }

    public void Dispose()
    {
        dispatchQueue.Dispose();
        lock (routesLock)
        {
            routes.Clear();
        }
    }

    private void Process(string commandText)
    {
        CommandInvocation invocation;
        Action<CommandInvocation>? handler;
        lock (routesLock)
        {
            if (!TryResolveDefinitionLocked(commandText, out var resolution, out _))
            {
                invocation = UnknownInvocation(commandText);
                handler = null;
            }
            else
            {
                invocation = resolution.Invocation;
                handler = resolution.Definition.Handler;
            }
        }

        if (handler is null)
        {
            RaiseSafely(Unhandled, invocation);
            return;
        }

        try
        {
            handler(invocation);
        }
        catch (Exception exception)
        {
            RaiseSafely(Failed, invocation, exception);
        }
    }

    private IReadOnlyList<CommandSuggestion> SuggestLocked(string commandText, int maximumResults)
    {
        var parsed = Parse(commandText);
        var domains = routes.Values
            .Select(static route => route.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(static domain => domain, StringComparer.Ordinal)
            .ToArray();
        if (parsed.Tokens.Count == 0)
        {
            return domains.Take(maximumResults).Select(DomainSuggestion).ToArray();
        }

        var domainPrefix = parsed.Tokens[0].Value;
        var domain = domains.FirstOrDefault(
            candidate => string.Equals(candidate, domainPrefix, StringComparison.OrdinalIgnoreCase));
        if (parsed.Tokens.Count == 1 && !parsed.EndsWithWhitespace)
        {
            return domains
                .Where(candidate => candidate.StartsWith(domainPrefix, StringComparison.OrdinalIgnoreCase))
                .Take(maximumResults)
                .Select(DomainSuggestion)
                .ToArray();
        }
        if (domain is null)
        {
            return [];
        }

        var surfaceSegments = parsed.Tokens.Skip(1).Select(static token => token.Value).ToArray();
        var parentCount = parsed.EndsWithWhitespace
            ? surfaceSegments.Length
            : Math.Max(0, surfaceSegments.Length - 1);
        var surfaceParent = surfaceSegments.Take(parentCount).ToArray();
        var partial = parsed.EndsWithWhitespace || surfaceSegments.Length == 0
            ? string.Empty
            : surfaceSegments[^1];
        var effectiveText = surfaceParent.Length == 0
            ? domain
            : $"{domain} {string.Join(' ', surfaceParent)}";
        if (!TryRewriteLinksLocked(effectiveText, out var rewrittenParent, out _))
        {
            return [];
        }

        var effective = Parse(rewrittenParent);
        if (effective.Tokens.Count == 0)
        {
            return [];
        }
        var effectiveDomain = effective.Tokens[0].Value;
        var effectiveParent = effective.Tokens.Skip(1).Select(static token => token.Value).ToArray();
        var labels = routes.Values
            .Where(route => string.Equals(
                route.Domain,
                effectiveDomain,
                StringComparison.OrdinalIgnoreCase))
            .Select(route => (Route: route, Segments: PathSegments(route.Path)))
            .Where(item => item.Segments.Length > effectiveParent.Length
                && PrefixEquals(item.Segments, effectiveParent))
            .Select(item => item.Segments[effectiveParent.Length])
            .Where(label => label.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(static label => label, StringComparer.Ordinal)
            .Take(maximumResults)
            .ToArray();

        var suggestions = new List<CommandSuggestion>(labels.Length);
        foreach (var label in labels)
        {
            var effectivePath = JoinPath(effectiveParent.Append(label));
            routes.TryGetValue(RouteKey(effectiveDomain, effectivePath), out var exactRoute);
            var surfacePath = JoinPath(surfaceParent.Append(label));
            var executionText = $"{domain} {surfacePath}";
            var canExecute = TryResolveDefinitionLocked(executionText, out _, out _);
            var kind = exactRoute?.Kind ?? CommandRouteKind.Command;
            var targetPath = exactRoute switch
            {
                { Kind: CommandRouteKind.Alias, Definition: not null } alias =>
                    $"{alias.Definition.CanonicalDomain} {alias.Definition.CanonicalPath}",
                { Kind: CommandRouteKind.Link, LinkTarget: { } target } =>
                    $"{target.Domain} {target.Path}",
                _ => null,
            };
            suggestions.Add(new(
                label,
                $"/{domain} {surfacePath} ",
                executionText,
                canExecute,
                kind,
                targetPath));
        }

        if (suggestions.Count < maximumResults
            && TryResolveDefinitionLocked(effectiveText, out var optionResolution, out _))
        {
            var usedArguments = Parse(optionResolution.Invocation.Arguments).Tokens
                .Select(static token => token.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var option in optionResolution.Definition.Options)
            {
                if (!option.Repeatable && option.Names.Any(usedArguments.Contains))
                {
                    continue;
                }

                foreach (var name in option.Names)
                {
                    if (!name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var surfacePrefix = surfaceParent.Length == 0
                        ? domain
                        : $"{domain} {JoinPath(surfaceParent)}";
                    suggestions.Add(new(
                        name,
                        $"/{surfacePrefix} {name} ",
                        $"{surfacePrefix} {name}",
                        false,
                        CommandRouteKind.Option,
                        null,
                        option.Description));
                    if (suggestions.Count >= maximumResults)
                    {
                        return suggestions;
                    }
                }
            }
        }
        return suggestions;
    }

    private static CommandSuggestion DomainSuggestion(string domain) => new(
        domain,
        $"/{domain} ",
        domain,
        false,
        CommandRouteKind.Domain,
        null);

    private bool TryResolveDefinitionLocked(
        string commandText,
        out CommandResolution resolution,
        out string? error)
    {
        resolution = default!;
        if (!TryRewriteLinksLocked(commandText, out var rewrittenText, out error))
        {
            return false;
        }

        var original = Parse(commandText);
        var rewritten = Parse(rewrittenText);
        if (rewritten.Tokens.Count < 2)
        {
            return false;
        }

        var route = FindLongestDefinitionRouteLocked(rewritten);
        if (route?.Definition is null)
        {
            return false;
        }

        var routeSegmentCount = PathSegments(route.Path).Length;
        var arguments = RemainderAfterPath(rewritten, routeSegmentCount);
        var invokedRoute = FindLongestRouteLocked(original);
        var invokedPath = invokedRoute?.Path
            ?? (original.Tokens.Count > 1 ? original.Tokens[1].Value : string.Empty);
        var definition = route.Definition;
        var resolvedText = $"{definition.CanonicalDomain} {definition.CanonicalPath}"
            + (arguments.Length == 0 ? string.Empty : $" {arguments}");
        resolution = new(
            definition,
            new(
                original.Text,
                resolvedText,
                definition.CanonicalDomain,
                invokedPath,
                definition.CanonicalPath,
                arguments));
        error = null;
        return true;
    }

    private bool TryRewriteLinksLocked(
        string commandText,
        out string rewrittenText,
        out string? error)
    {
        rewrittenText = Parse(commandText).Text;
        error = null;
        var visited = new HashSet<CommandRoute>(ReferenceEqualityComparer.Instance);
        for (var hop = 0; hop < MaximumLinkHops; hop++)
        {
            var parsed = Parse(rewrittenText);
            var link = FindLongestLinkRouteLocked(parsed);
            if (link is null)
            {
                return true;
            }
            if (!visited.Add(link))
            {
                error = "Command link cycle detected.";
                return false;
            }

            var target = link.LinkTarget!.Value;
            var remainder = RemainderAfterPath(parsed, PathSegments(link.Path).Length);
            rewrittenText = $"{target.Domain} {target.Path}"
                + (remainder.Length == 0 ? string.Empty : $" {remainder}");
        }

        error = "Command link depth exceeded.";
        return false;
    }

    private CommandRoute? FindLongestDefinitionRouteLocked(ParsedInput input) =>
        FindLongestRouteLocked(input, static route => route.Definition is not null);

    private CommandRoute? FindLongestLinkRouteLocked(ParsedInput input) =>
        FindLongestRouteLocked(input, static route => route.Kind == CommandRouteKind.Link);

    private CommandRoute? FindLongestRouteLocked(ParsedInput input) =>
        FindLongestRouteLocked(input, static _ => true);

    private CommandRoute? FindLongestRouteLocked(
        ParsedInput input,
        Func<CommandRoute, bool> predicate)
    {
        if (input.Tokens.Count < 2)
        {
            return null;
        }

        var domain = input.Tokens[0].Value;
        return routes.Values
            .Where(route => predicate(route)
                && string.Equals(route.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Select(route => (Route: route, Segments: PathSegments(route.Path)))
            .Where(item => PrefixEqualsInput(item.Segments, input))
            .OrderByDescending(static item => item.Segments.Length)
            .Select(static item => item.Route)
            .FirstOrDefault();
    }

    private bool HasPathLocked(CommandAddress address) => routes.Values.Any(route =>
        string.Equals(route.Domain, address.Domain, StringComparison.OrdinalIgnoreCase)
        && (string.Equals(route.Path, address.Path, StringComparison.OrdinalIgnoreCase)
            || route.Path.StartsWith(address.Path + " ", StringComparison.OrdinalIgnoreCase)));

    private bool HasDescendantLocked(CommandAddress address) => routes.Values.Any(route =>
        string.Equals(route.Domain, address.Domain, StringComparison.OrdinalIgnoreCase)
        && route.Path.StartsWith(address.Path + " ", StringComparison.OrdinalIgnoreCase));

    private bool HasLinkAncestorLocked(CommandAddress address) => routes.Values.Any(route =>
        route.Kind == CommandRouteKind.Link
        && string.Equals(route.Domain, address.Domain, StringComparison.OrdinalIgnoreCase)
        && address.Path.StartsWith(route.Path + " ", StringComparison.OrdinalIgnoreCase));

    private static CommandInvocation UnknownInvocation(string commandText)
    {
        var parsed = Parse(commandText);
        var domain = parsed.Tokens.Count == 0 ? string.Empty : parsed.Tokens[0].Value;
        var path = parsed.Tokens.Count < 2
            ? string.Empty
            : JoinPath(parsed.Tokens.Skip(1).Select(static token => token.Value));
        return new(
            parsed.Text,
            parsed.Text,
            domain,
            path,
            path,
            string.Empty);
    }

    private static string RemainderAfterPath(ParsedInput input, int pathSegmentCount)
    {
        var nextToken = 1 + pathSegmentCount;
        return nextToken >= input.Tokens.Count
            ? string.Empty
            : input.Text[input.Tokens[nextToken].Start..];
    }

    private static bool PrefixEqualsInput(string[] routeSegments, ParsedInput input)
    {
        if (input.Tokens.Count - 1 < routeSegments.Length)
        {
            return false;
        }
        for (var index = 0; index < routeSegments.Length; index++)
        {
            if (!string.Equals(
                    routeSegments[index],
                    input.Tokens[index + 1].Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool PrefixEquals(string[] value, string[] prefix)
    {
        if (value.Length < prefix.Length)
        {
            return false;
        }
        for (var index = 0; index < prefix.Length; index++)
        {
            if (!string.Equals(value[index], prefix[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static string FirstToken(string commandText)
    {
        var parsed = Parse(commandText);
        return parsed.Tokens.Count == 0 ? string.Empty : parsed.Tokens[0].Value;
    }

    internal static string NormalizeDomain(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Any(char.IsWhiteSpace) || normalized.Contains('/'))
        {
            throw new ArgumentException(
                "A command domain must be one segment without '/'.",
                parameterName);
        }
        return normalized;
    }

    internal static string NormalizePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var segments = value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment.Contains('/')))
        {
            throw new ArgumentException(
                "A command path must contain non-empty segments without '/'.",
                parameterName);
        }
        return string.Join(' ', segments);
    }

    private static CommandAddress NormalizeAddress(
        string domain,
        string path,
        string domainParameterName,
        string pathParameterName) => new(
            NormalizeDomain(domain, domainParameterName),
            NormalizePath(path, pathParameterName));

    private static void ValidateMode(CommandRegistrationMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static ParsedInput Parse(string value)
    {
        var text = value.Trim();
        var endsWithWhitespace = value.Length > 0 && char.IsWhiteSpace(value[^1]);
        var tokens = new List<CommandToken>();
        for (var index = 0; index < text.Length;)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
            if (index >= text.Length)
            {
                break;
            }
            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }
            tokens.Add(new(text[start..index], start));
        }
        return new(text, tokens, endsWithWhitespace);
    }

    private static string[] PathSegments(string path) => path.Split(' ');

    private static string JoinPath(IEnumerable<string> segments) => string.Join(' ', segments);

    private static string RouteKey(string domain, string path) => $"{domain}\0{path}";

    private static void RaiseSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
            }
        }
    }

    private static void RaiseSafely<T1, T2>(Action<T1, T2>? handlers, T1 first, T2 second)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (Action<T1, T2> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(first, second);
            }
            catch
            {
            }
        }
    }

    private sealed class CommandDefinition(
        string canonicalDomain,
        string canonicalPath,
        Action<CommandInvocation> handler,
        IReadOnlyList<CommandOption> options)
    {
        internal string CanonicalDomain { get; } = canonicalDomain;

        internal string CanonicalPath { get; } = canonicalPath;

        internal Action<CommandInvocation> Handler { get; set; } = handler;

        internal IReadOnlyList<CommandOption> Options { get; set; } = options;
    }

    private sealed class CommandRoute
    {
        private CommandRoute(
            string domain,
            string path,
            CommandRouteKind kind,
            CommandDefinition? definition,
            CommandAddress? linkTarget)
        {
            Domain = domain;
            Path = path;
            Kind = kind;
            Definition = definition;
            LinkTarget = linkTarget;
        }

        internal string Domain { get; }

        internal string Path { get; }

        internal CommandRouteKind Kind { get; }

        internal CommandDefinition? Definition { get; }

        internal CommandAddress? LinkTarget { get; }

        internal static CommandRoute Command(
            string domain,
            string path,
            CommandDefinition definition) =>
            new(domain, path, CommandRouteKind.Command, definition, null);

        internal static CommandRoute Alias(
            string domain,
            string path,
            CommandDefinition definition) =>
            new(domain, path, CommandRouteKind.Alias, definition, null);

        internal static CommandRoute Link(
            string domain,
            string path,
            CommandAddress target) =>
            new(domain, path, CommandRouteKind.Link, null, target);
    }

    private readonly record struct CommandAddress(string Domain, string Path);

    private sealed record CommandResolution(
        CommandDefinition Definition,
        CommandInvocation Invocation);

    private sealed record CommandToken(string Value, int Start);

    private sealed record ParsedInput(
        string Text,
        IReadOnlyList<CommandToken> Tokens,
        bool EndsWithWhitespace);
}
