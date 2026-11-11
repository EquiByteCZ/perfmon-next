using System.Text.Json;

namespace PerfMonNext.Core;

public static class WinRmScripts
{
    public static string ListSets(string computer, bool useCredential)
    {
        var body = """
            $sets = @(Get-Counter -ListSet * | ForEach-Object {
              [pscustomobject]@{
                name = $_.CounterSetName
                help = [string]$_.Description
                counters = @($_.Counter)
                paths = @($_.Paths)
              }
            })
            $sets | ConvertTo-Json -Compress -Depth 4
            """;
        return Wrap(computer, useCredential, body);
    }

    public static string Sample(string computer, IReadOnlyList<string> paths, bool useCredential)
    {
        var literals = string.Join(",\n", paths.Select(path => "  '" + Escape(path) + "'"));
        var body = """
            $paths = @(
            """ + literals + """
            )
            $sample = Get-Counter -Counter $paths
            $rows = @($sample.CounterSamples | ForEach-Object { [pscustomobject]@{ path = $_.Path; value = [double]$_.CookedValue } })
            $rows | ConvertTo-Json -Compress
            """;
        return Wrap(computer, useCredential, body);
    }

    public static string CredentialPreamble(string userName) =>
        "$sec = ConvertTo-SecureString $env:PERFMON_WINRM_PASSWORD -AsPlainText -Force\n" +
        "$cred = New-Object System.Management.Automation.PSCredential('" + Escape(userName) + "', $sec)\n" +
        "Remove-Item Env:PERFMON_WINRM_PASSWORD -ErrorAction SilentlyContinue\n";

    private static string Wrap(string computer, bool useCredential, string body)
    {
        var header = "$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n";
        if (MachineHost.IsLocal(computer))
            return header + body;

        var cred = useCredential ? "-Credential $cred " : "";
        return header +
            "Invoke-Command -ComputerName '" + Escape(computer) + "' " + cred +
            "-ScriptBlock {\n" + body + "\n}\n";
    }

    public static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

public static class WinRmJson
{
    public static CounterCatalog ParseSets(string json, string host)
    {
        using var document = JsonDocument.Parse(ExtractJson(json));
        var root = document.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        var sets = new List<CounterSetInfo>();
        foreach (var element in elements)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("name", out var nameProperty))
                continue;
            var name = nameProperty.GetString() ?? "";
            if (name.Length == 0)
                continue;
            var help = element.TryGetProperty("help", out var helpProperty) ? helpProperty.GetString() : "";
            var rows = new List<CounterSetBuilder.SampleRow>();
            var multi = false;
            foreach (var path in ReadStrings(element, "counters").Concat(ReadStrings(element, "paths")))
            {
                if (!CounterPathParser.TryParse(path, out var parsed) || parsed is null)
                    continue;
                if (parsed.Instance == "*")
                    multi = true;
                rows.Add(new CounterSetBuilder.SampleRow(parsed.Counter, parsed.Instance));
            }

            sets.Add(CounterSetBuilder.Build(name, help, rows, multi));
        }

        sets.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return new CounterCatalog { SourceHost = host, Sets = sets };
    }

    public static IReadOnlyList<(CounterPath Path, double Value)> ParseSamples(string json)
    {
        var text = ExtractJson(json);
        if (text is "null" or "" or "[]")
            return [];
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        var samples = new List<(CounterPath, double)>();
        foreach (var element in elements)
        {
            if (!element.TryGetProperty("path", out var pathProperty))
                continue;
            var pathText = pathProperty.GetString() ?? "";
            if (!CounterPathParser.TryParse(pathText, out var path) || path is null)
                continue;
            if (!element.TryGetProperty("value", out var valueProperty))
                continue;
            var value = valueProperty.ValueKind == JsonValueKind.String
                ? double.Parse(valueProperty.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : valueProperty.GetDouble();
            if (!double.IsFinite(value))
                continue;
            samples.Add((path with { Machine = null }, value));
        }

        return samples;
    }

    public static string ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("WinRM returned no JSON.");
        var startArray = text.IndexOf('[');
        var startObject = text.IndexOf('{');
        var start = startArray < 0 ? startObject : startObject < 0 ? startArray : Math.Min(startArray, startObject);
        if (start < 0)
            throw new FormatException("WinRM output did not contain JSON.");
        var end = Math.Max(text.LastIndexOf(']'), text.LastIndexOf('}'));
        if (end < start)
            throw new FormatException("WinRM output did not contain JSON.");
        return text[start..(end + 1)];
    }

    private static IEnumerable<string> ReadStrings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            yield break;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is string text && text.Length > 0)
                yield return text;
        }
    }
}

public sealed class RoutingCounterProbe : ICounterProbe
{
    private readonly ICounterProbe _pdh;
    private readonly ICounterProbe _winRm;

    public RoutingCounterProbe(ICounterProbe pdh, ICounterProbe winRm)
    {
        _pdh = pdh;
        _winRm = winRm;
    }

    public MachineReadResult Read(MachineTarget machine, IReadOnlyList<CounterPath> paths)
    {
        if (machine.Connection == ConnectionMode.WinRm)
            return _winRm.Read(machine, paths);

        var result = _pdh.Read(machine, paths);
        if (MachineHost.IsLocal(machine.Host) || result.Status is not (MachineStatus.Offline or MachineStatus.AccessDenied or MachineStatus.Error))
            return result;

        try
        {
            var fallback = _winRm.Read(machine, paths);
            if (fallback.Status != MachineStatus.Online)
                return result with { Message = result.Message + " WinRM also failed: " + fallback.Message };
            machine.Connection = ConnectionMode.WinRm;
            return fallback with { Message = "PDH was blocked (" + result.Message + "). Sampling through WinRM." };
        }
        catch (Exception ex)
        {
            return result with { Message = result.Message + " WinRM fallback failed: " + ex.Message };
        }
    }

    public void Release(string machineId)
    {
        _pdh.Release(machineId);
        _winRm.Release(machineId);
    }
}
