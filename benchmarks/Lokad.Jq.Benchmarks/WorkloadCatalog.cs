using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lokad.Jq.Benchmarking;

internal enum ComparisonKind { Bytes, JsonValues }
internal sealed record ComparisonWorkload(string Name, string Scale, int Size,
    string[] Arguments, byte[] Input, ComparisonKind Comparison);

internal static class WorkloadCatalog
{
    public static IReadOnlyList<ComparisonWorkload> Create()
    {
        var cases = new List<ComparisonWorkload>();
        foreach (var (scale, count) in new[] { ("small", 16), ("medium", 256), ("large", 4096) })
        {
            int walkCount = scale == "large" ? 512 : count;
            int unicodeCount = scale == "large" ? 1024 : count;
            int constructionCount = scale == "large" ? 512 : count;
            byte[] array = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, count)
                .Select(i => new { id = i, key = (i * 17) % 31, text = "item-" + i.ToString(CultureInfo.InvariantCulture) }));
            byte[] ndjson = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, count)
                .Select(i => JsonSerializer.Serialize(new { id = i, key = (i * 17) % 31 }) + "\n")));
            byte[] numbers = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, count).ToArray());
            byte[] walk = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, walkCount)
                .Select(i => new { id = i, key = (i * 17) % 31, text = "item-" + i.ToString(CultureInfo.InvariantCulture) }));
            byte[] construction = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, constructionCount)
                .Select(i => new { id = i, key = (i * 17) % 31, text = "item-" + i.ToString(CultureInfo.InvariantCulture) }));
            byte[] unicode = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Range(0, unicodeCount)
                .Select(i => "\"café 🚀 世界-" + i.ToString(CultureInfo.InvariantCulture) + "\"")) + "]");
            Add("identity-compact", ".", array, ComparisonKind.Bytes, true, false, count);
            Add("identity-pretty", ".", array, ComparisonKind.Bytes, false, false, count);
            Add("ndjson-project", "{id, doubled:(.id * 2)}", ndjson, ComparisonKind.JsonValues, true, false, count);
            Add("ndjson-select", "select(.key % 3 == 0) | .id", ndjson, ComparisonKind.JsonValues, true, false, count);
            Add("map-construct", "map({name:.text, value:(.id * 2)})", construction, ComparisonKind.JsonValues, true, false, constructionCount);
            Add("reduce", "reduce .[] as $x (0; . + $x)", numbers, ComparisonKind.JsonValues, true, false, count);
            Add("foreach", "foreach .[] as $x (0; . + $x; .)", numbers, ComparisonKind.JsonValues, true, false, count);
            Add("sort-group", "sort_by(.key) | group_by(.key) | map({key:.[0].key, count:length})",
                construction, ComparisonKind.JsonValues, true, false, constructionCount);
            Add("entries-update", "map(.id += 1 | del(.text) | to_entries | from_entries)",
                construction, ComparisonKind.JsonValues, true, false, constructionCount);
            Add("walk-paths", "walk(if type == \"number\" then . + 1 else . end) | [paths]",
                walk, ComparisonKind.JsonValues, true, false, walkCount);
            Add("unicode-strings", ".[] | explode | implode | split(\"-\") | join(\"/\")",
                unicode, ComparisonKind.Bytes, false, true, unicodeCount);
            Add("json-roundtrip", "map(tojson | fromjson)", construction, ComparisonKind.JsonValues, true, false, constructionCount);

            void Add(string name, string filter, byte[] input, ComparisonKind comparison, bool compact, bool raw, int size)
            {
                // Windows jq needs -b to preserve LF. Both options are supported
                // inert flags in the embedding runtime and are explicit in both lanes.
                var arguments = new List<string> { "-b", "-M" };
                if (compact) arguments.Add("-c");
                if (raw) arguments.Add("-r");
                arguments.Add(filter);
                cases.Add(new(name, scale, size, arguments.ToArray(), input, comparison));
            }
        }
        // Separate launch/compilation controls; never subtract these timings.
        cases.Add(new("startup-empty", "control", 0, ["-b", "-M", "-n", "empty"], [], ComparisonKind.Bytes));
        cases.Add(new("small-request", "control", 1, ["-b", "-M", "-c", ".id"], "{\"id\":7}"u8.ToArray(), ComparisonKind.Bytes));
        return cases;
    }
}
