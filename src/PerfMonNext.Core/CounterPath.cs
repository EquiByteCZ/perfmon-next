namespace PerfMonNext.Core;

/// <summary>
/// A Windows counter path: \\machine\category(instance)\counter.
/// Machine and instance may be empty. Instance may be "*" or a prefix wildcard such as "chrome*".
/// </summary>
public sealed record CounterPath(string Category, string Counter, string Instance, string? Machine = null)
{
    public string Format()
    {
        var instance = string.IsNullOrEmpty(Instance) ? "" : "(" + Instance + ")";
        var body = "\\" + Category + instance + "\\" + Counter;
        return string.IsNullOrWhiteSpace(Machine) ? body : "\\\\" + Machine.Trim() + body;
    }
}

public static class CounterPathParser
{
    public static CounterPath Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Counter path is empty.");

        var s = text.Trim();
        string? machine = null;
        if (s.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var slash = s.IndexOf('\\', 2);
            if (slash < 0)
                throw new FormatException($"Counter path '{text}' is missing a category.");
            machine = s[2..slash];
            s = s[slash..];
        }

        if (!s.StartsWith('\\'))
            throw new FormatException($"Counter path '{text}' must start with \\ or \\\\machine\\.");

        s = s[1..];
        var split = s.LastIndexOf('\\');
        if (split <= 0 || split == s.Length - 1)
            throw new FormatException($"Counter path '{text}' must include a category and a counter.");

        var counter = s[(split + 1)..].Trim();
        var left = s[..split].Trim();
        if (counter.Length == 0 || left.Length == 0)
            throw new FormatException($"Counter path '{text}' must include a category and a counter.");

        string category;
        var instance = "";
        var paren = left.IndexOf('(');
        if (paren >= 0 && left.EndsWith(')'))
        {
            category = left[..paren].Trim();
            instance = left[(paren + 1)..^1];
        }
        else
        {
            category = left;
        }

        if (category.Length == 0)
            throw new FormatException($"Counter path '{text}' is missing a category.");

        return new CounterPath(category, counter, instance, machine);
    }

    public static bool TryParse(string text, out CounterPath? path)
    {
        try
        {
            path = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            path = null;
            return false;
        }
    }
}

public static class InstanceMatcher
{
    /// <summary>
    /// True for aggregate instances that sum the others: "_Total", "Total", ".NET"'s "_Global_",
    /// and per-group totals such as "0,_Total" in Processor Information.
    /// </summary>
    public static bool IsAggregate(string? instance)
    {
        if (string.IsNullOrWhiteSpace(instance))
            return false;
        var value = instance.Trim();
        return value.Equals("_Total", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Total", StringComparison.OrdinalIgnoreCase)
            || value.Equals("_Global_", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "*" means every instance except aggregates (_Total and the like), so a chart does not mix the sum with its parts.
    /// Tick _Total separately to see it. A prefix such as "chrome*" matches by prefix; anything else must match exactly.
    /// </summary>
    public static bool Matches(string? pattern, string? instance)
    {
        pattern ??= "";
        instance ??= "";
        if (pattern.Length == 0)
            return instance.Length == 0;
        if (pattern == "*")
            return !IsAggregate(instance);
        if (pattern.EndsWith('*') && pattern.Length > 1)
        {
            var prefix = pattern[..^1];
            return instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, instance, StringComparison.OrdinalIgnoreCase);
    }
}

public static class MachineHost
{
    public static bool IsLocal(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;
        var value = host.Trim();
        return value.Equals(".", StringComparison.OrdinalIgnoreCase)
            || value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value.Equals("(local)", StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string? host) => IsLocal(host) ? "." : host!.Trim();

    public static string ToCounterMachine(string? host) => IsLocal(host) ? "." : host!.Trim();

    public static string Display(string? host, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName.Trim();
        return IsLocal(host) ? "Local" : host!.Trim();
    }

    public static string ShortName(string? host)
    {
        var value = Normalize(host);
        if (value == ".")
            return value;
        if (System.Net.IPAddress.TryParse(value, out _))
            return value;
        var cut = value.IndexOf('.');
        return cut <= 0 ? value : value[..cut];
    }

    public static bool SameHost(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;
        return ShortName(a).Equals(ShortName(b), StringComparison.OrdinalIgnoreCase);
    }
}

public static class Ids
{
    public static string New(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];
}
