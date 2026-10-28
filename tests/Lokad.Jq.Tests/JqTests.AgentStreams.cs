using System.Globalization;
using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("closed")]
    [InlineData("cancelled")]
    [InlineData("refused")]
    public async Task Jq_CompoundLogStreamPreservesBoundedAdmissionAndCleanup(string outcome)
    {
        const int count = 5000;
        var rows = Enumerable.Range(0, count).Select(i => new
        {
            service = "service-" + (i % 8).ToString(CultureInfo.InvariantCulture),
            severity = i % 3 == 0 ? "error" : "info",
            http = i % 7 == 0 ? null : new { code = 400 + i % 5 },
            message = "request:" + (outcome == "refused" && i == 3
                ? new string('x', 512 * 1024) : i.ToString(CultureInfo.InvariantCulture))
        }).ToArray();
        string input = string.Concat(rows.Select(row => JsonSerializer.Serialize(row) + "\n"));
        string expected = string.Concat(rows.Where(row => row.severity == "error").Select(row =>
            row.service + "\t" + (row.http?.code ?? 0).ToString(CultureInfo.InvariantCulture)
            + "\t" + row.message["request:".Length..] + "\n"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var host = new MockFileSystem { AppendRemainsOpen = outcome != "closed" };
        host.AddFile("/events", input);
        if (outcome == "cancelled") host.BeforeByteAppend = () =>
        {
            if (host.AppendCallCount > 0) cancellation.Cancel();
        };
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "select(.severity == \"error\") | [.service, (.http.code // 0), (.message|split(\":\")|last)] | @tsv", "/events")));

        if (outcome == "cancelled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        else
            Assert.Equal(outcome == "refused" ? 5 : 0,
                await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));

        string output = host.GetOutput(JqFileDescriptor.StdOut);
        if (outcome == "complete") Assert.Equal(expected, output);
        else
        {
            Assert.NotEmpty(output);
            Assert.StartsWith(output, expected, StringComparison.Ordinal);
            if (outcome != "refused")
                Assert.True(host.ReadBytesCallCount < 5, "Closure/cancellation must stop the long input near its first output.");
        }
        if (outcome == "closed") Assert.Equal(1, host.AppendCallCount);
        Assert.Equal(outcome == "refused" ? "jq: memory budget exceeded\n" : "", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
