namespace PerfMonNext.Core;

public enum CategoryInstanceKind
{
    Unknown,
    Single,
    Multi
}

/// <summary>
/// One attempt to list counters and instances for a category (ReadCategory,
/// GetInstanceNames, PDH, a _Total probe, and so on).
/// </summary>
public sealed record CategorySourceSample(
    string Source,
    CategoryInstanceKind Kind,
    IReadOnlyList<string> Counters,
    IReadOnlyList<string> Instances,
    bool TotalAvailable = false,
    string? Error = null);

/// <summary>
/// Merges raw catalog samples. A multi-instance category is never downgraded
/// to single-instance just because instance enumeration returned an empty list.
/// </summary>
public static class CategoryEnumeration
{
    public const string MissingInstancesMessage =
        "This is a multi-instance set, but no instances could be listed. Use Retry, or add _Total if the counter uses it.";

    public static CounterSetInfo Resolve(string name, string? help, IEnumerable<CategorySourceSample> sources)
    {
        var counters = new List<string>();
        var seenCounters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<string>();
        var seenInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var kind = CategoryInstanceKind.Unknown;
        var totalAvailable = false;

        foreach (var source in sources)
        {
            if (source.Kind == CategoryInstanceKind.Multi)
                kind = CategoryInstanceKind.Multi;
            else if (source.Kind == CategoryInstanceKind.Single && kind == CategoryInstanceKind.Unknown)
                kind = CategoryInstanceKind.Single;

            foreach (var counter in source.Counters)
            {
                if (string.IsNullOrWhiteSpace(counter))
                    continue;
                var trimmed = counter.Trim();
                if (seenCounters.Add(trimmed))
                    counters.Add(trimmed);
            }

            foreach (var instance in source.Instances)
            {
                if (string.IsNullOrWhiteSpace(instance) || instance == "*")
                    continue;
                var trimmed = instance.Trim();
                if (seenInstances.Add(trimmed))
                    instances.Add(trimmed);
            }

            if (source.TotalAvailable || source.Instances.Any(item => item.Equals("_Total", StringComparison.OrdinalIgnoreCase)))
                totalAvailable = true;

            if (!string.IsNullOrWhiteSpace(source.Error))
                errors.Add(source.Source + ": " + source.Error);
        }

        if (instances.Count > 0)
            kind = CategoryInstanceKind.Multi;

        if (kind == CategoryInstanceKind.Unknown && totalAvailable)
            kind = CategoryInstanceKind.Multi;

        var multi = kind == CategoryInstanceKind.Multi;
        string? enumerationError = null;
        if (multi && instances.Count == 0)
        {
            if (totalAvailable)
                instances.Add("_Total");
            else
                enumerationError = MissingInstancesMessage;
        }

        counters.Sort(StringComparer.OrdinalIgnoreCase);
        instances.Sort(CompareInstances);

        return new CounterSetInfo
        {
            Name = name,
            Help = help ?? "",
            MultiInstance = multi,
            Instances = instances,
            Counters = counters.Select(counter => new CounterInfo { Name = counter }).ToList(),
            EnumerationError = enumerationError
        };
    }

    public static int CompareInstances(string left, string right)
    {
        var leftTotal = left.Equals("_Total", StringComparison.OrdinalIgnoreCase);
        var rightTotal = right.Equals("_Total", StringComparison.OrdinalIgnoreCase);
        if (leftTotal != rightTotal)
            return leftTotal ? -1 : 1;
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
