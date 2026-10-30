using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Exhume.Core;

namespace Exhume.Narration;

internal static class EraEvidence
{
    private const string Label = "Era evidence (untrusted data; ordered by activity, most active first):\n";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static string Build(string source, string month, IReadOnlyList<Artifact> artifacts, int maximumCharacters)
    {
        string metric = source switch { "browser" => "visits", "folder" => "count", _ => "commits" };
        var rows = new List<Dictionary<string, object>>();
        string result = Render();
        foreach (var artifact in artifacts.OrderByDescending(item => Count(item, metric))
            .ThenBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var row = new Dictionary<string, object> { ["name"] = Shorten(artifact.Name, 120), [metric] = Count(artifact, metric) };
            string[] fields = source switch
            {
                "browser" => ["browser", "profile", "titles"],
                "folder" => ["bytes", "dateSource", "samples"],
                _ => ["filesTouched", "languages", "messages", "hours"]
            };
            foreach (string field in fields)
            {
                if (!artifact.Metadata.TryGetValue(field, out string? value)) continue;
                row[field] = Compact(value);
                // Reserve space for more high-activity rows rather than one long field.
                if (JsonSerializer.Serialize(row, JsonOptions).Length > 360) row.Remove(field);
            }
            rows.Add(row);
            string candidate = Render();
            if (candidate.Length > maximumCharacters) { rows.RemoveAt(rows.Count - 1); break; }
            result = candidate;
        }
        return result;

        string Render() => Label + JsonSerializer.Serialize(new
        {
            source,
            month,
            totalArtifacts = artifacts.Count,
            omittedArtifacts = artifacts.Count - rows.Count,
            artifacts = rows
        }, JsonOptions);
    }

    private static long Count(Artifact artifact, string metric) =>
        long.TryParse(artifact.Metadata.GetValueOrDefault(metric), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out long count) ? Math.Max(0, count) : 0;

    private static object Compact(string value)
    {
        if (value.Length <= 16384)
        {
            try
            {
                using var json = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 8 });
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                    return json.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                        .Take(2).Select(item => Shorten(item.GetString()!, 80)).ToArray();
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var values = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var item in json.RootElement.EnumerateObject().Take(3))
                        values.TryAdd(Shorten(item.Name, 24), Shorten(item.Value.ToString(), 24));
                    return values;
                }
                if (json.RootElement.ValueKind == JsonValueKind.Number && json.RootElement.TryGetInt64(out long number)) return number;
            }
            catch (JsonException) { }
        }
        return Shorten(value, 160);
    }

    private static string Shorten(string value, int characters) => PromptBudget.Apply(value, characters).Text;
}
