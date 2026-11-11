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

    /// <summary>
    /// False when the catalog only knows the set name. The picker must load counters
    /// and instances before it can show them.
    /// </summary>
    public bool DetailsLoaded { get; init; } = true;
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
                Counters = counters,
                DetailsLoaded = set.DetailsLoaded
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

public static class CounterSetBuilder
{
    public readonly record struct SampleRow(string Counter, string Instance);

    public static bool NeedsDetailLoad(CounterSetInfo set) => !set.DetailsLoaded;

    public static CounterSetInfo Build(string name, string? help, IReadOnlyList<SampleRow> rows, bool multiHint = false)
    {
        var counters = new List<CounterInfo>();
        var seenCounters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<string>();
        var seenInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Counter) && seenCounters.Add(row.Counter))
                counters.Add(new CounterInfo { Name = row.Counter.Trim() });
            if (!string.IsNullOrWhiteSpace(row.Instance)
                && row.Instance != "*"
                && seenInstances.Add(row.Instance))
                instances.Add(row.Instance.Trim());
        }

        counters.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        instances.Sort((left, right) =>
        {
            var leftTotal = left.Equals("_Total", StringComparison.OrdinalIgnoreCase);
            var rightTotal = right.Equals("_Total", StringComparison.OrdinalIgnoreCase);
            if (leftTotal != rightTotal)
                return leftTotal ? -1 : 1;
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        });

        return new CounterSetInfo
        {
            Name = name,
            Help = help ?? "",
            MultiInstance = multiHint || instances.Count > 0,
            Instances = instances,
            Counters = counters,
            DetailsLoaded = true
        };
    }
}
