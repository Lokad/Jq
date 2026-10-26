using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("sh", true)]
    [InlineData("sh", false)]
    [InlineData("sh", null)]
    [InlineData("csv", true)]
    [InlineData("csv", false)]
    [InlineData("csv", null)]
    [InlineData("tsv", true)]
    [InlineData("tsv", false)]
    [InlineData("tsv", null)]
    public void Jq_ScalarFormattingAdmitsOutputWithoutPerFieldSerialization(string format, bool? value)
    {
        const int count = 2500;
        var row = new JsonArray();
        for (int index = 0; index < count; index++) row.Add(value.HasValue ? JsonValue.Create(value.Value) : null);
        string separator = format == "sh" ? " " : format == "csv" ? "," : "\t";
        string spelling = value.HasValue ? value.Value ? "true" : "false" : format == "sh" ? "null" : "";
        string original = row.ToJsonString();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        {
            Assert.Equal(string.Join(separator, Enumerable.Repeat(spelling, count)), new JqRuntime(budget).Format(format, row));
            Assert.Equal(original, row.ToJsonString());
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("sh")]
    [InlineData("csv")]
    public void Jq_ScalarFormattingStillRefusesActualOutputConstruction(string format)
    {
        var row = new JsonArray();
        for (int index = 0; index < 8000; index++) row.Add(true);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
            Assert.Throws<JqQuotaException>(() => new JqRuntime(budget).Format(format, row));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("[true,false,null,1.5,\"a'b\"]|@sh", "true false null 1.5 'a'\\''b'\n")]
    [InlineData("[true,false,null,nan,1.5,\"a,\\\"b\"]|@csv", "true,false,,,1.5,\"a,\"\"b\"\n")]
    [InlineData("null|@sh", "null\n")]
    [InlineData("false|@sh", "false\n")]
    [InlineData("[true,false,null]|@sh \"args: \\(.)\"", "args: true false null\n")]
    [InlineData("[true,false,null]|@csv \"row: \\(.)\"", "row: true,false,\n")]
    public async Task Jq_ScalarFormattingKeepsSpellingsQuotingAndTemplates(string filter, string expected)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("sh", "stream")]
    [InlineData("csv", "stream")]
    [InlineData("sh", "closed")]
    [InlineData("csv", "closed")]
    [InlineData("sh", "cancelled")]
    [InlineData("csv", "cancelled")]
    public async Task Jq_ScalarFormattingKeepsStreamsBoundedAndStopsAtHostBoundaries(string format, string mode)
    {
        const int records = 2500;
        using var cancellation = new CancellationTokenSource();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = mode != "closed" };
        host.AddFile("/input", string.Concat(Enumerable.Repeat("null\n", records)));
        host.AddFile("/later", "null\n");
        if (mode == "cancelled") host.BeforeByteAppend = () => { if (host.AppendCallCount == 2) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "[true,false,null]|@" + format, "/input", "/later")));
        if (mode == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        else Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        string line = format == "sh" ? "true false null\n" : "true,false,\n";
        Assert.Equal(string.Concat(Enumerable.Repeat(line, mode == "stream" ? records + 1 : mode == "closed" ? 1 : 2)),
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(mode == "stream" ? 2 : 1, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
