using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lokad.Jq.Benchmarking;

// Public, synthetic fixtures. Inputs and independent expectations are built
// before measurement; the existing runners consume the same scenario definitions.
internal static class AgentWorkloads
{
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static IReadOnlyList<GovernorWorkload> Create()
    {
        var cases = new List<GovernorWorkload>();
        var noError = OutputDigest.FromBytes([]);
        foreach (var (scale, count) in new[] { ("small", 16), ("medium", 128), ("large", 1024) })
        {
            Api(scale, count);
            Logs(scale, scale == "large" ? 4096 : count);
            Inventory(scale, count);
            Summary(scale, count);
            Configuration(scale, count);
            Difference(scale, count);
            Tree(scale, count);
            Regex(scale, count);
        }
        return cases;

        void Add(string name, string scale, int count, string[] flags, string filter,
            byte[] input, byte[] expected, ComparisonKind comparison)
        {
            var workload = new ComparisonWorkload(name, scale, count,
                flags.Append(filter).ToArray(), input, comparison);
            cases.Add(new(workload, OutputDigest.FromBytes(expected), noError));
        }

        void Api(string scale, int count)
        {
            var issues = Enumerable.Range(0, count).Select(i => new
            {
                id = i, title = "café request " + Number(i), state = i % 4 == 0 ? "closed" : "open",
                owner = i % 5 == 0 ? null : new { login = "user-" + Number(i % 7) },
                labels = i % 11 == 0 ? null : new[] { i % 3 == 0 ? "docs" : "ops", "team:b", "team:a", "team:a" }
            }).ToArray();
            var pages = issues.Chunk(32).Select(items => new { items }).ToArray();
            var expected = issues.Where(issue => issue.state == "open" && (issue.labels ?? []).Contains("ops"))
                .Select(issue => new { issue.id, issue.title, owner = issue.owner?.login ?? "unassigned", labels = new[] { "team:a", "team:b" } });
            Add("api-shaping", scale, count, ["-b", "-Mc"],
                ".pages[].items[] | select(.state == \"open\" and ((.labels // []) | index(\"ops\"))) | " +
                "{id,title,owner:(.owner.login // \"unassigned\"),labels:((.labels // []) | map(select(startswith(\"team:\"))) | unique)}",
                Input(new { pages }), Stream(expected), ComparisonKind.JsonValues);
        }

        void Logs(string scale, int count)
        {
            var records = Enumerable.Range(0, count).Select(i => new
            {
                service = "service-" + Number(i % 8), severity = new[] { "info", "warn", "error" }[i % 3],
                http = i % 7 == 0 ? null : new { duration_ms = i % 97 }
            }).ToArray();
            var expected = records.Where(record => record.severity != "info")
                .GroupBy(record => record.service + ":" + record.severity)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new { key = group.Key, count = group.Count(), total_ms = group.Sum(record => record.http?.duration_ms ?? 0) });
            Add("incident-logs", scale, count, ["-b", "-Mnc"],
                "reduce inputs as $r ({}; if $r.severity == \"info\" then . else " +
                "($r.service + \":\" + $r.severity) as $k | .[$k] = ((.[$k] // {count:0,total_ms:0}) | " +
                ".count += 1 | .total_ms += ($r.http.duration_ms // 0)) end) | " +
                "to_entries | sort_by(.key) | map({key,count:.value.count,total_ms:.value.total_ms})",
                Stream(records), Output(expected), ComparisonKind.JsonValues);
        }

        void Inventory(string scale, int count)
        {
            int distinct = Math.Max(4, count / 4);
            var resources = Enumerable.Range(0, distinct + 4).Select(i => new
            {
                id = Number(i % distinct), name = "resource-" + Number(i), owner = "team-" + Number(i % 5)
            }).ToArray();
            var events = Enumerable.Range(0, count).Select(i => new { @event = i, resource_id = Number(i * 7 % (distinct + 3)) }).ToArray();
            var index = resources.GroupBy(resource => resource.id).ToDictionary(group => group.Key, group => group.Last());
            var expected = events.Select(item =>
            {
                index.TryGetValue(item.resource_id, out var resource);
                return new { item.@event, item.resource_id, owner = resource?.owner ?? "unknown", resource = resource?.name };
            });
            Add("inventory-enrichment", scale, count, ["-b", "-Mc"],
                "(.resources | INDEX(.id)) as $by_id | .events[] | " +
                "{event,resource_id,owner:($by_id[.resource_id].owner // \"unknown\"),resource:($by_id[.resource_id].name // null)}",
                Input(new { resources, events }), Stream(expected), ComparisonKind.JsonValues);
        }

        void Summary(string scale, int count)
        {
            var rows = Enumerable.Range(0, count).Select(i => new
            {
                owner = "team-" + Number(i % 7), status = new[] { "open", "waiting", "closed" }[i % 3], duration = i * 17 % 101
            }).ToArray();
            var expected = rows.GroupBy(row => (row.owner, row.status)).Select(group => new
            {
                group.Key.owner, group.Key.status, count = group.Count(), total = group.Sum(row => row.duration), max = group.Max(row => row.duration)
            }).OrderByDescending(row => row.count).ThenBy(row => row.owner, StringComparer.Ordinal)
                .ThenBy(row => row.status, StringComparer.Ordinal).Take(10);
            Add("operational-summary", scale, count, ["-b", "-Mc"],
                "group_by([.owner,.status]) | map({owner:.[0].owner,status:.[0].status,count:length," +
                "total:(map(.duration)|add),max:(map(.duration)|max)}) | sort_by(-.count,.owner,.status) | .[:10]",
                Input(rows), Output(expected), ComparisonKind.JsonValues);
        }

        void Configuration(string scale, int count)
        {
            var services = Enumerable.Range(0, count).Select(i => new
            {
                id = i, enabled = i % 3 == 0 ? (bool?)null : i % 2 == 0,
                timeout_ms = i % 4 == 0 ? (int?)null : 100 + i,
                settings = new Dictionary<string, object?> { ["mode"] = i % 2 == 0 ? "fast" : "safe", ["unused"] = null }
            }).ToArray();
            var defaults = new { region = "west", http = new { timeout_ms = 1000, retries = 3 },
                credentials = new { token = "sample" }, metadata = new { version = 1, debug = true } };
            var expected = new { region = "west", http = new { timeout_ms = 2000, retries = 3 }, metadata = new { version = 1 },
                services = services.Select(service => new { service.id, enabled = service.enabled ?? true,
                    timeout_ms = (service.timeout_ms ?? 250) + 50, settings = new { mode = service.settings["mode"] } }) };
            Add("configuration-updates", scale, count, ["-b", "-Mc"],
                "((.defaults * .overrides) | delpaths([[\"credentials\"],[\"metadata\",\"debug\"]])) + " +
                "{services:(.services | map({id,enabled:(if has(\"enabled\") then .enabled else true end)," +
                "timeout_ms:((.timeout_ms // 250)+50),settings:(.settings | with_entries(select(.value != null)))}))}",
                Input(new { defaults, overrides = new { http = new { timeout_ms = 2000 } }, services }),
                Output(expected), ComparisonKind.JsonValues);
        }

        void Difference(string scale, int count)
        {
            var before = Enumerable.Range(0, count).Select(i => new { id = Number(i), state = "ready", revision = i }).ToArray();
            var after = Enumerable.Range(count / 8, count).Select(i => new { id = Number(i), state = "ready", revision = i + (i % 3 == 0 ? 1 : 0) }).ToArray();
            var left = before.ToDictionary(row => row.id);
            var right = after.ToDictionary(row => row.id);
            var expected = left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id =>
            {
                left.TryGetValue(id, out var oldValue);
                right.TryGetValue(id, out var newValue);
                return new { id, kind = oldValue is null ? "added" : newValue is null ? "removed" : "changed", before = oldValue, after = newValue };
            }).Where(row => row.before?.revision != row.after?.revision);
            Add("snapshot-differences", scale, count, ["-b", "-Mc"],
                "(.before | INDEX(.id)) as $b | (.after | INDEX(.id)) as $a | " +
                "[(($b|keys)+($a|keys)|unique)[] as $id | select($b[$id] != $a[$id]) | " +
                "{id:$id,kind:(if ($b|has($id)|not) then \"added\" elif ($a|has($id)|not) then \"removed\" else \"changed\" end),before:$b[$id],after:$a[$id]}]",
                Input(new { before, after }), Output(expected), ComparisonKind.JsonValues);
        }

        void Tree(string scale, int count)
        {
            var services = Enumerable.Range(0, count).Select(i => new { name = "service-" + Number(i),
                checks = Enumerable.Range(0, 3).Select(j => new { name = "check-" + Number(j), duration_ms = (i + j) % 97, healthy = (i + j) % 5 != 0 }).ToArray() }).ToArray();
            string expected = string.Concat(services.SelectMany((service, i) => service.checks.SelectMany((check, j) => new[]
            {
                $"services.{Number(i)}.checks.{Number(j)}.duration_ms\t{Number(check.duration_ms)}\n",
                $"services.{Number(i)}.checks.{Number(j)}.healthy\t{(check.healthy ? "true" : "false")}\n"
            })));
            Add("nested-extraction", scale, count, ["-b", "-Mr"],
                "paths(type == \"number\" or type == \"boolean\") as $p | " +
                "select($p[-1] == \"duration_ms\" or $p[-1] == \"healthy\") | [($p|map(tostring)|join(\".\")),getpath($p)] | @tsv",
                Input(new { services }), Encoding.UTF8.GetBytes(expected), ComparisonKind.Bytes);
        }

        void Regex(string scale, int count)
        {
            var rows = Enumerable.Range(0, count).Select(i => new { kind = i / 16 % 2 == 0 ? "http" : "worker",
                level = i % 3 == 0 ? "ERROR" : "INFO", service = i / 16 % 2 == 0 ? "api" : "worker", id = i,
                text = "request token=sample" + Number(i) + " café" }).ToArray();
            var input = rows.Select(row => new { row.kind, line = row.kind == "http"
                ? $"{row.level} {row.service} id={Number(row.id)} {row.text}"
                : $"{row.service}|{row.level}|{Number(row.id)}|{row.text}" });
            var expected = rows.Select(row => new { row.level, row.service, row.id, text = "request token=*** café" });
            Add("regex-log-cleanup", scale, count, ["-b", "-Mc"],
                ".[] | (if .kind == \"http\" then .line | capture(\"^(?<level>[A-Z]+) (?<service>[a-z]+) id=(?<id>[0-9]+) (?<text>.*)$\") " +
                "else .line | capture(\"^(?<service>[a-z]+)\\\\|(?<level>[A-Z]+)\\\\|(?<id>[0-9]+)\\\\|(?<text>.*)$\") end) | " +
                "{level,service,id:(.id|tonumber),text:(.text|gsub(\"token=[A-Za-z0-9]+\";\"token=***\"))}",
                Input(input), Stream(expected), ComparisonKind.JsonValues);
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static byte[] Input<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, InputOptions);
    private static byte[] Output<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, OutputOptions) + "\n");
    private static byte[] Stream<T>(IEnumerable<T> rows) => Encoding.UTF8.GetBytes(string.Concat(
        rows.Select(row => JsonSerializer.Serialize(row, OutputOptions) + "\n")));
}
