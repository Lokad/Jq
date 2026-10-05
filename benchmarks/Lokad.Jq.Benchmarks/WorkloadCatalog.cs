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
        foreach (var (scale, count) in new[] { ("small", 16), ("medium", 128), ("large", 512) })
        {
            byte[] array = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, count)
                .Select(i => new { id = i, key = (i * 17) % 31, text = "item-" + i.ToString(CultureInfo.InvariantCulture) }));
            byte[] ndjson = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, count)
                .Select(i => JsonSerializer.Serialize(new { id = i, key = (i * 17) % 31 }) + "\n")));
            byte[] numbers = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, count).ToArray());
            byte[] unicode = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Range(0, count)
                .Select(i => "\"café 🚀 世界-" + i.ToString(CultureInfo.InvariantCulture) + "\"")) + "]");
            Add("identity-compact", ".", array, ComparisonKind.Bytes, true, false);
            Add("identity-pretty", ".", array, ComparisonKind.Bytes, false, false);
            Add("ndjson-project", "{id, doubled:(.id * 2)}", ndjson, ComparisonKind.JsonValues, true, false);
            Add("ndjson-select", "select(.key % 3 == 0) | .id", ndjson, ComparisonKind.JsonValues, true, false);
            Add("map-construct", "map({name:.text, value:(.id * 2)})", array, ComparisonKind.JsonValues, true, false);
            Add("reduce", "reduce .[] as $x (0; . + $x)", numbers, ComparisonKind.JsonValues, true, false);
            Add("foreach", "foreach .[] as $x (0; . + $x; .)", numbers, ComparisonKind.JsonValues, true, false);
            Add("sort-group", "sort_by(.key) | group_by(.key) | map({key:.[0].key, count:length})",
                array, ComparisonKind.JsonValues, true, false);
            Add("entries-update", "map(.id += 1 | del(.text) | to_entries | from_entries)",
                array, ComparisonKind.JsonValues, true, false);
            Add("walk-paths", "walk(if type == \"number\" then . + 1 else . end) | [paths]",
                array, ComparisonKind.JsonValues, true, false);
            Add("unicode-strings", ".[] | explode | implode | split(\"-\") | join(\"/\")",
                unicode, ComparisonKind.Bytes, false, true);
            Add("json-roundtrip", "map(tojson | fromjson)", array, ComparisonKind.JsonValues, true, false);

            void Add(string name, string filter, byte[] input, ComparisonKind comparison, bool compact, bool raw)
            {
                // Windows jq needs -b to preserve LF. Both options are supported
                // inert flags in the embedding runtime and are explicit in both lanes.
                var arguments = new List<string> { "-b", "-M" };
                if (compact) arguments.Add("-c");
                if (raw) arguments.Add("-r");
                arguments.Add(filter);
                cases.Add(new(name, scale, count, arguments.ToArray(), input, comparison));
            }
        }
        // Separate launch/compilation controls; never subtract these timings.
        cases.Add(new("startup-empty", "control", 0, ["-b", "-M", "-n", "empty"], [], ComparisonKind.Bytes));
        cases.Add(new("small-request", "control", 1, ["-b", "-M", "-c", ".id"], "{\"id\":7}"u8.ToArray(), ComparisonKind.Bytes));
        return cases;
    }
}
