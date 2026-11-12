using System.Diagnostics;
using PerfMonNext.Core;

namespace PerfMonNext.Windows;

public static class WindowsCategorySampler
{
    public static void RefreshMachine(string pcMachine)
    {
        try
        {
            PdhCatalog.RefreshObjects(pcMachine);
        }
        catch
        {
            // Refresh is best-effort. Enumeration methods still run.
        }
    }

    public static CounterSetInfo ReadSet(string pcMachine, string categoryName)
    {
        var sources = new List<CategorySourceSample>();
        PerformanceCounterCategory? category = null;
        try
        {
            category = new PerformanceCounterCategory(categoryName, pcMachine);
            var kind = KindOf(category.CategoryType);
            sources.Add(new CategorySourceSample("CategoryType", kind, [], []));
        }
        catch (Exception ex)
        {
            sources.Add(new CategorySourceSample("CategoryType", CategoryInstanceKind.Unknown, [], [], Error: ex.Message));
        }

        sources.Add(FromReadCategory(category, pcMachine, categoryName));
        sources.Add(FromInstanceNames(category, pcMachine, categoryName));
        sources.Add(FromPdh(pcMachine, categoryName));
        sources.Add(FromTotalProbe(pcMachine, categoryName));
        return CategoryEnumeration.Resolve(categoryName, null, sources);
    }

    public static string[] ReadInstanceNames(string pcMachine, string categoryName)
    {
        var set = ReadSet(pcMachine, categoryName);
        return set.Instances.ToArray();
    }

    private static CategoryInstanceKind KindOf(PerformanceCounterCategoryType type) => type switch
    {
        PerformanceCounterCategoryType.MultiInstance => CategoryInstanceKind.Multi,
        PerformanceCounterCategoryType.SingleInstance => CategoryInstanceKind.Single,
        _ => CategoryInstanceKind.Unknown
    };

    private static CategorySourceSample FromReadCategory(PerformanceCounterCategory? existing, string pcMachine, string categoryName)
    {
        try
        {
            var category = existing ?? new PerformanceCounterCategory(categoryName, pcMachine);
            var counters = new List<string>();
            var instances = new List<string>();
            var seenCounters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InstanceDataCollection column in category.ReadCategory().Values)
            {
                if (!string.IsNullOrWhiteSpace(column.CounterName) && seenCounters.Add(column.CounterName.Trim()))
                    counters.Add(column.CounterName.Trim());
                foreach (InstanceData sample in column.Values)
                {
                    if (string.IsNullOrWhiteSpace(sample.InstanceName))
                        continue;
                    var name = sample.InstanceName.Trim();
                    if (seenInstances.Add(name))
                        instances.Add(name);
                }
            }

            return new CategorySourceSample("ReadCategory", CategoryInstanceKind.Unknown, counters, instances);
        }
        catch (Exception ex)
        {
            return new CategorySourceSample("ReadCategory", CategoryInstanceKind.Unknown, [], [], Error: ex.Message);
        }
    }

    private static CategorySourceSample FromInstanceNames(PerformanceCounterCategory? existing, string pcMachine, string categoryName)
    {
        PerformanceCounter[]? counters = null;
        try
        {
            var category = existing ?? new PerformanceCounterCategory(categoryName, pcMachine);
            if (category.CategoryType == PerformanceCounterCategoryType.SingleInstance)
            {
                counters = category.GetCounters();
                return new CategorySourceSample(
                    "GetCounters",
                    CategoryInstanceKind.Single,
                    counters.Select(counter => counter.CounterName).ToArray(),
                    []);
            }

            string[] instances;
            try
            {
                instances = category.GetInstanceNames();
            }
            catch (InvalidOperationException ex)
            {
                return new CategorySourceSample("GetInstanceNames", CategoryInstanceKind.Multi, [], [], Error: ex.Message);
            }

            if (instances.Length == 0)
                return new CategorySourceSample("GetInstanceNames", CategoryInstanceKind.Multi, [], []);

            var probe = instances.FirstOrDefault(name => name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) ?? instances[0];
            counters = category.GetCounters(probe);
            var names = counters.Select(counter => counter.CounterName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new CategorySourceSample("GetInstanceNames", CategoryInstanceKind.Multi, names, instances);
        }
        catch (Exception ex)
        {
            return new CategorySourceSample("GetInstanceNames", CategoryInstanceKind.Unknown, [], [], Error: ex.Message);
        }
        finally
        {
            if (counters is not null)
            {
                foreach (var counter in counters)
                    counter.Dispose();
            }
        }
    }

    private static CategorySourceSample FromPdh(string pcMachine, string categoryName)
    {
        if (!PdhCatalog.TryEnumItems(pcMachine, categoryName, out var counters, out var instances, out var error))
            return new CategorySourceSample("PdhEnumObjectItems", CategoryInstanceKind.Unknown, [], [], Error: error);
        var kind = instances.Length > 0 ? CategoryInstanceKind.Multi : CategoryInstanceKind.Unknown;
        return new CategorySourceSample("PdhEnumObjectItems", kind, counters, instances);
    }

    private static CategorySourceSample FromTotalProbe(string pcMachine, string categoryName)
    {
        PerformanceCounter[]? counters = null;
        try
        {
            var category = new PerformanceCounterCategory(categoryName, pcMachine);
            if (category.CategoryType == PerformanceCounterCategoryType.SingleInstance)
                return new CategorySourceSample("TotalProbe", CategoryInstanceKind.Single, [], []);
            counters = category.GetCounters("_Total");
            var names = counters.Select(counter => counter.CounterName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new CategorySourceSample("TotalProbe", CategoryInstanceKind.Multi, names, ["_Total"], TotalAvailable: true);
        }
        catch (Exception)
        {
            return new CategorySourceSample("TotalProbe", CategoryInstanceKind.Unknown, [], []);
        }
        finally
        {
            if (counters is not null)
            {
                foreach (var counter in counters)
                    counter.Dispose();
            }
        }
    }
}
