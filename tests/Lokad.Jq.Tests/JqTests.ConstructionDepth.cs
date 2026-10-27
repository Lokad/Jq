using System.Globalization;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    public static IEnumerable<object[]> RecursiveConstructionCases()
    {
        string[] operations =
        [
            "[0] | map($v)", "[0] | map_values($v)", "{a:0} | map_values($v)",
            "$v | to_entries", "$v | group_by(.)", "$v | _group_by_impl(.)",
            "{a:0} | with_entries(.value=$v)", "INDEX($v;\"x\")",
            "null | setpath([0,\"key\"];\"a\") | setpath([0,\"value\"] + [range(DEPTH)|0];0) | from_entries",
            "null | setpath([range(DEPTH+2)|0];0) | combinations",
        ];
        string[] copies =
        [
            ". + [0]", ". - []", "add(.,.)", "sort", "unique", "reverse", "flatten(0)",
            "sort_by(empty)", "unique_by(empty)", "_sort_by_impl([0])", "_unique_by_impl([0])",
        ];
        foreach (int depth in new[] { 63, 64 })
        {
            string digits = depth.ToString(CultureInfo.InvariantCulture);
            foreach (string operation in operations)
                yield return [depth, "null | setpath([range(" + digits + ")|0];0) as $v | ("
                    + operation.Replace("DEPTH", digits, StringComparison.Ordinal) + ")"];
            foreach (string operation in copies)
                yield return [depth, "null | setpath([range(" + digits + "+1)|0];0) | (" + operation + ")"];
        }
    }

    [Theory]
    [MemberData(nameof(RecursiveConstructionCases))]
    public async Task Jq_RecursiveConstructionChecksDestinationDepthBeforeScalarConsumers(int depth, string source)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "\"prefix\", (try (" + source + " | length) catch \"caught\")")));
        Assert.Equal(depth == 63 ? 0 : 5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        string result = (source.Contains("add(.,.)", StringComparison.Ordinal) || source.Contains(". + [0]", StringComparison.Ordinal)) ? "2\n" : "1\n";
        Assert.Equal(depth == 63 ? "\"prefix\"\n" + result : "\"prefix\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(depth == 63 ? "" : "jq: value nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_BoundaryDepthConstructionStreamsWithinSharedAllowance()
    {
        const int count = 1000;
        string record = new string('[', 63) + "0" + new string(']', 63) + "\n";
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat(record, count)));
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "to_entries | length")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("1\n", count)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
