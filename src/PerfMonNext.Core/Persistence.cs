using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfMonNext.Core;

public sealed class WorkspaceFormatException : Exception
{
    public WorkspaceFormatException(string message) : base(message) { }
}

public sealed record WorkspaceLoadResult(Workspace Workspace, IReadOnlyList<string> Warnings);

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public static class WorkspaceStore
{
    public static string Serialize(Workspace workspace)
    {
        WorkspaceSanitizer.Sanitize(workspace);
        return JsonSerializer.Serialize(workspace, JsonDefaults.Options);
    }

    public static void Save(string path, Workspace workspace)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, Serialize(workspace));
    }

    public static WorkspaceLoadResult Deserialize(string json)
    {
        var warnings = new List<string>();
        if (ContainsPassword(json))
            warnings.Add("A password was present in the file and was ignored. Enter it again for this session.");

        Workspace workspace;
        try
        {
            workspace = JsonSerializer.Deserialize<Workspace>(json, JsonDefaults.Options)
                ?? throw new WorkspaceFormatException("Workspace file is empty.");
        }
        catch (JsonException ex)
        {
            throw new WorkspaceFormatException("Could not read the workspace file. " + ex.Message);
        }

        if (workspace.Version > 1)
            throw new WorkspaceFormatException($"Workspace version {workspace.Version} is newer than this build.");

        warnings.AddRange(WorkspaceSanitizer.Sanitize(workspace));
        return new WorkspaceLoadResult(workspace, warnings);
    }

    public static WorkspaceLoadResult Load(string path) => Deserialize(File.ReadAllText(path));

    private static bool ContainsPassword(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("machines", out var machines))
                return false;
            foreach (var machine in machines.EnumerateArray())
            {
                if (machine.TryGetProperty("password", out _))
                    return true;
                if (machine.TryGetProperty("credential", out var credential)
                    && credential.ValueKind == JsonValueKind.Object
                    && credential.TryGetProperty("password", out _))
                    return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}

public sealed record ImportedSeries(
    string Machine,
    string Category,
    string Counter,
    string Instance,
    IReadOnlyList<SamplePoint> Samples);

public sealed record CsvImportResult(IReadOnlyList<ImportedSeries> Series, IReadOnlyList<string> Warnings);

public static class CsvExchange
{
    public static string Export(IEnumerable<SeriesSnapshot> series)
    {
        var builder = new StringBuilder();
        builder.AppendLine("timestamp,machine,category,counter,instance,value");
        foreach (var snapshot in series)
        {
            foreach (var sample in snapshot.Samples)
            {
                builder.Append(Escape(sample.Timestamp.ToString("o", CultureInfo.InvariantCulture))).Append(',');
                builder.Append(Escape(snapshot.Host)).Append(',');
                builder.Append(Escape(snapshot.Path.Category)).Append(',');
                builder.Append(Escape(snapshot.Path.Counter)).Append(',');
                builder.Append(Escape(snapshot.Path.Instance)).Append(',');
                if (sample.Value is double value && double.IsFinite(value))
                    builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    public static void Save(string path, IEnumerable<SeriesSnapshot> series)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, Export(series));
    }

    public static CsvImportResult Import(string csvText)
    {
        var rows = CsvTable.Parse(csvText);
        if (rows.Count == 0)
            return new CsvImportResult([], ["The file has no rows."]);

        if (rows[0].Length > 0 && rows[0][0].Contains("PDH-CSV", StringComparison.OrdinalIgnoreCase))
            return ImportPdh(rows);

        return ImportSimple(rows);
    }

    private static CsvImportResult ImportSimple(List<string[]> rows)
    {
        var warnings = new List<string>();
        var header = rows[0];
        var map = Index(header);
        foreach (var name in new[] { "timestamp", "machine", "category", "counter", "instance", "value" })
        {
            if (!map.ContainsKey(name))
                throw new FormatException($"CSV is missing the '{name}' column.");
        }

        var grouped = new Dictionary<string, List<SamplePoint>>(StringComparer.OrdinalIgnoreCase);
        var meta = new Dictionary<string, (string Machine, string Category, string Counter, string Instance)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Length == 1 && row[0].Length == 0)
                continue;
            if (!TryGet(row, map["timestamp"], out var stampText) || !TryTimestamp(stampText, out var stamp))
            {
                warnings.Add($"Skipped row {i + 1}: timestamp '{stampText}' was not recognized.");
                continue;
            }

            TryGet(row, map["machine"], out var machine);
            TryGet(row, map["category"], out var category);
            TryGet(row, map["counter"], out var counter);
            TryGet(row, map["instance"], out var instance);
            TryGet(row, map["value"], out var valueText);
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(counter))
            {
                warnings.Add($"Skipped row {i + 1}: category and counter are required.");
                continue;
            }

            double? value = null;
            if (!string.IsNullOrWhiteSpace(valueText))
            {
                if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    warnings.Add($"Skipped the value on row {i + 1}.");
                }
                else
                {
                    value = parsed;
                }
            }

            var key = string.Join('\u001f', machine, category, counter, instance);
            if (!grouped.TryGetValue(key, out var list))
            {
                list = [];
                grouped[key] = list;
                meta[key] = (machine, category, counter, instance);
            }

            list.Add(new SamplePoint(stamp, value));
        }

        return new CsvImportResult(Materialize(grouped, meta), warnings);
    }

    private static CsvImportResult ImportPdh(List<string[]> rows)
    {
        var warnings = new List<string>();
        var header = rows[0];
        if (!header[0].Contains("(UTC)", StringComparison.OrdinalIgnoreCase))
            warnings.Add("PDH-CSV timestamps were read as timestamps without a zone and stored as UTC.");

        var columns = new List<(int Index, CounterPath Path, string Machine)>();
        for (var i = 1; i < header.Length; i++)
        {
            if (!CounterPathParser.TryParse(header[i], out var path) || path is null)
            {
                warnings.Add($"Skipped PDH column '{header[i]}'.");
                continue;
            }

            columns.Add((i, path, string.IsNullOrWhiteSpace(path.Machine) ? "." : path.Machine));
        }

        var grouped = new Dictionary<string, List<SamplePoint>>(StringComparer.OrdinalIgnoreCase);
        var meta = new Dictionary<string, (string Machine, string Category, string Counter, string Instance)>(StringComparer.OrdinalIgnoreCase);
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.Length == 0 || !TryTimestamp(row[0], out var stamp))
            {
                warnings.Add($"Skipped PDH row {rowIndex + 1}: timestamp was not recognized.");
                continue;
            }

            foreach (var column in columns)
            {
                var text = column.Index < row.Length ? row[column.Index] : "";
                double? value = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        continue;
                    value = parsed;
                }

                var key = string.Join('\u001f', column.Machine, column.Path.Category, column.Path.Counter, column.Path.Instance);
                if (!grouped.TryGetValue(key, out var list))
                {
                    list = [];
                    grouped[key] = list;
                    meta[key] = (column.Machine, column.Path.Category, column.Path.Counter, column.Path.Instance);
                }

                list.Add(new SamplePoint(stamp, value));
            }
        }

        return new CsvImportResult(Materialize(grouped, meta), warnings);
    }

    private static List<ImportedSeries> Materialize(
        Dictionary<string, List<SamplePoint>> grouped,
        Dictionary<string, (string Machine, string Category, string Counter, string Instance)> meta)
    {
        var series = new List<ImportedSeries>();
        foreach (var pair in grouped)
        {
            var info = meta[pair.Key];
            var samples = pair.Value.OrderBy(sample => sample.Timestamp).ToList();
            series.Add(new ImportedSeries(info.Machine, info.Category, info.Counter, info.Instance, samples));
        }

        return series;
    }

    private static Dictionary<string, int> Index(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
            map[header[i].Trim()] = i;
        return map;
    }

    private static bool TryGet(string[] row, int index, out string value)
    {
        if (index < 0 || index >= row.Length)
        {
            value = "";
            return false;
        }

        value = row[index];
        return true;
    }

    public static bool TryTimestamp(string text, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
            return true;

        string[] formats =
        [
            "MM/dd/yyyy HH:mm:ss.fff",
            "M/d/yyyy HH:mm:ss.fff",
            "M/d/yyyy H:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss.fff",
            "dd/MM/yyyy HH:mm:ss.fff"
        ];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
            || DateTime.TryParseExact(text, formats, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out exact))
        {
            timestamp = new DateTimeOffset(DateTime.SpecifyKind(exact, DateTimeKind.Utc));
            return true;
        }

        return false;
    }

    public static string Escape(string? value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return value;
    }
}

public static class CsvTable
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"')
                quoted = true;
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || (row.Count == 1 && row[0].Length > 0))
                    rows.Add(row.ToArray());
                row.Clear();
            }
            else if (ch != '\r')
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }
}

public sealed record BlgPlan(string BlgPath, string CsvPath, IReadOnlyList<string> Arguments, string CommandLine);

public static class BlgImportPlanner
{
    public static bool LooksLikeBlg(string path, ReadOnlySpan<byte> header)
    {
        if (path.EndsWith(".blg", StringComparison.OrdinalIgnoreCase))
            return true;
        if (header.Length == 0)
            return false;
        if (header[0] == '"' || StartsWithAscii(header, "timestamp") || StartsWithAscii(header, "(PDH-CSV"))
            return false;
        return IndexOfAscii(header, "PDH") >= 0;
    }

    public static BlgPlan Create(string blgPath)
    {
        var directory = Path.GetDirectoryName(blgPath);
        var csvName = Path.GetFileNameWithoutExtension(blgPath) + ".csv";
        var csvPath = string.IsNullOrEmpty(directory) ? csvName : Path.Combine(directory, csvName);
        IReadOnlyList<string> arguments = [blgPath, "-f", "CSV", "-o", csvPath, "-y"];
        var command = "relog.exe " + string.Join(' ', arguments.Select(Quote));
        return new BlgPlan(blgPath, csvPath, arguments, command);
    }

    public static string Quote(string value) =>
        value.Contains(' ') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;

    private static bool StartsWithAscii(ReadOnlySpan<byte> header, string text)
    {
        if (header.Length < text.Length)
            return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (header[i] != (byte)text[i])
                return false;
        }

        return true;
    }

    private static int IndexOfAscii(ReadOnlySpan<byte> header, string text)
    {
        var limit = Math.Min(header.Length, 512);
        var window = header[..limit];
        for (var i = 0; i <= window.Length - text.Length; i++)
        {
            var match = true;
            for (var j = 0; j < text.Length; j++)
            {
                if (window[i + j] != (byte)text[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }
}
