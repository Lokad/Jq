using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("%c", 12000)]
    [InlineData("%x", 4000)]
    [InlineData("%X", 4000)]
    public async Task Jq_DateConstructionRepeatedCompositesFitTheirOutputAllowance(string directive, int length)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "format",
            string.Concat(Enumerable.Repeat(directive, 500)), "0|strftime($format)|length")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("%c", "Thu Jan  1 00:00:00 1970")]
    [InlineData("%x", "01/01/70")]
    [InlineData("%X", "00:00:00")]
    public async Task Jq_DateConstructionPreservesCompleteCompositeContents(string directive, string expected)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(96 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "format",
            string.Concat(Enumerable.Repeat(directive, 500)), "0|strftime($format)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat(expected, 500)) + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false, "Thu Jan  1 00:00:00 1970|01/01/70|00:00:00|0|+0000|UTC|%Q|%|%\n")]
    [InlineData(true, "Thu Jan  1 05:30:00 1970|01/01/70|05:30:00|0|+0530|PLUS530|%Q|%|%\n")]
    public async Task Jq_DateConstructionPreservesZoneAndUnknownDirectivePolicies(bool local, string expected)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("PLUS530", TimeSpan.FromHours(5.5), "PLUS530", "PLUS530");
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildZonedInvocation(zone, "-nr", "--arg", "format",
            "%c|%x|%X|%s|%z|%Z|%Q|%%|%", "0|" + (local ? "strflocaltime" : "strftime") + "($format)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_DateConstructionDispatchesBorrowedDirectivesWithoutTemporaryStrings()
    {
        JsonNode input = JsonValue.Create(string.Concat(Enumerable.Repeat("1970", 10000)));
        JsonNode format = JsonValue.Create(string.Concat(Enumerable.Repeat("%Y", 10000)));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), budget))
        {
            _ = JqTime.Strptime(context, JsonValue.Create("1970"), JsonValue.Create("%Y"));
            long before = GC.GetAllocatedBytesForCurrentThread();
            JsonNode value = JqTime.Strptime(context, input, format);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal("[1970,0,0,0,0,0,8,367]", value.ToJsonString());
            Assert.InRange(allocated, 0, 8192);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("%c")]
    [InlineData("%x")]
    public async Task Jq_DateConstructionKeepsNecessaryOutputRefusalTerminal(string directive)
    {
        var host = new MockFileSystem();
        host.AddFile("/records", "0\n");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "--arg", "format",
            string.Concat(Enumerable.Repeat(directive, 5000)), "\"ready\", (try strftime($format) catch \"caught\")", "/records")));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("closed")]
    [InlineData("cancelled")]
    public async Task Jq_DateConstructionStreamsRespectHostBoundaries(string mode)
    {
        foreach (bool local in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var host = new MockFileSystem { AppendRemainsOpen = mode != "closed" };
            host.AddFile("/records", string.Concat(Enumerable.Repeat("0\n", 200)));
            if (mode == "cancelled") host.BeforeByteAppend = () => { if (host.AppendCallCount == 2) cancellation.Cancel(); };
            var memory = new BenchmarkMemoryBudget(64 * 1024);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildZonedInvocation(TimeZoneInfo.Utc, "-r", "--arg", "format",
                "%c|%x|%X", (local ? "strflocaltime" : "strftime") + "($format)", "/records")));
            if (mode == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
            else Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
            int count = mode == "stream" ? 200 : mode == "closed" ? 1 : 2;
            Assert.Equal(string.Concat(Enumerable.Repeat("Thu Jan  1 00:00:00 1970|01/01/70|00:00:00\n", count)), host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Single(host.ClosedDescriptors);
            Assert.Equal(0, host.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
    }
}
