namespace PerfMonNext.Core;

public sealed class CounterInfo
{
    public required string Name { get; init; }
    public string Help { get; init; } = "";
}

public sealed class CounterSetInfo
{
    public required string Name { get; init; }
    public string Help { get; init; } = "";
    public bool MultiInstance { get; init; }
    public IReadOnlyList<string> Instances { get; init; } = [];
    public IReadOnlyList<CounterInfo> Counters { get; init; } = [];
}

public sealed class CounterCatalog
{
    public required string SourceHost { get; init; }
    public bool IsStarter { get; init; }
    public string? Warning { get; init; }
    public IReadOnlyList<CounterSetInfo> Sets { get; init; } = [];
}

public static class CatalogSearch
{
    public static IReadOnlyList<CounterSetInfo> Filter(CounterCatalog catalog, string? query)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
            return catalog.Sets;

        var result = new List<CounterSetInfo>();
        foreach (var set in catalog.Sets)
        {
            if (TokensMatch(set.Name + " " + set.Help, tokens))
            {
                result.Add(set);
                continue;
            }

            var counters = set.Counters.Where(counter =>
            {
                var haystack = string.Join(' ', set.Name, counter.Name, counter.Help, string.Join(' ', set.Instances));
                return TokensMatch(haystack, tokens);
            }).ToList();

            if (counters.Count == 0)
                continue;

            result.Add(new CounterSetInfo
            {
                Name = set.Name,
                Help = set.Help,
                MultiInstance = set.MultiInstance,
                Instances = set.Instances,
                Counters = counters
            });
        }

        return result;
    }

    public static List<string> Tokenize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        return query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .ToList();
    }

    public static bool TokensMatch(string haystack, IReadOnlyList<string> tokens)
    {
        var text = haystack.ToLowerInvariant();
        foreach (var token in tokens)
        {
            if (!text.Contains(token, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
