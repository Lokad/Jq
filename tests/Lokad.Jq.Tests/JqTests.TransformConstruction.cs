using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("ascii_downcase")]
    [InlineData("ascii_upcase")]
    [InlineData("html")]
    [InlineData("uri")]
    public void Jq_TransformsBorrowUnchangedPayloadsWithoutAdmission(string operation)
    {
        string text = new('0', 131072);
        JsonNode input = JsonValue.Create(text);
        var memory = new BenchmarkMemoryBudget(0);
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token);
        var runtime = new JqRuntime(budget);
        Assert.Same(text, Transform(runtime, operation, input));
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Transform(runtime, operation, input));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("ascii_downcase", 'A', "a")]
    [InlineData("ascii_upcase", 'a', "A")]
    [InlineData("html", '&', "&amp;")]
    [InlineData("uri", ' ', "%20")]
    public void Jq_TransformsAdmitOnlyTheChangedResult(string operation, char changed, string ending)
    {
        string text = new string('0', 32767) + changed;
        JsonNode input = JsonValue.Create(text);
        var memory = new BenchmarkMemoryBudget(80 * 1024);
        var counted = new CountingMemoryBudget(memory);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None))
        {
            Assert.Equal(new string('0', 32767) + ending, Transform(new JqRuntime(budget), operation, input));
            Assert.Equal(2 * (32767 + ending.Length), memory.PeakBytes);
            Assert.Equal(1, counted.Counts.Reservations + counted.Counts.Resizes);
            Assert.Equal(text, JqRuntime.String(input));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("ascii_downcase", 'A')]
    [InlineData("ascii_upcase", 'a')]
    [InlineData("html", '&')]
    [InlineData("uri", ' ')]
    public void Jq_TransformsCancelAfterAdmissionBeforeAllocatingOutput(string operation, char ch)
    {
        JsonNode input = JsonValue.Create(new string(ch, 131072));
        var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var callback = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, callback, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.ThrowsAny<OperationCanceledException>(() => Transform(runtime, operation, input));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("ascii_downcase", 'A')]
    [InlineData("ascii_upcase", 'a')]
    [InlineData("html", '&')]
    [InlineData("uri", ' ')]
    public void Jq_TransformsRefuseActualOutputBeforeAllocation(string operation, char ch)
    {
        JsonNode input = JsonValue.Create(new string(ch, 131072));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<JqQuotaException>(() => Transform(runtime, operation, input));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(1022)]
    [InlineData(1023)]
    [InlineData(1024)]
    public void Jq_UriConstructionPreservesPairsAndReplacementAcrossChunks(int prefix)
    {
        string text = new string('x', prefix) + "🚀\uD800\0";
        using var budget = new JqBudget(CancellationToken.None);
        Assert.Equal(new string('x', prefix) + "%F0%9F%9A%80%EF%BF%BD%00", new JqRuntime(budget).Format("uri", JsonValue.Create(text)));
    }

    [Theory]
    [InlineData("ascii_downcase")]
    [InlineData("ascii_upcase")]
    [InlineData("@html")]
    [InlineData("@uri")]
    public async Task Jq_TransformResultsKeepFreshPathIdentity(string operation)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\"abc\"\n\"\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "try (path(" + operation + ")|true) catch false")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("false\nfalse\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("ascii_downcase", "stream")]
    [InlineData("ascii_upcase", "stream")]
    [InlineData("html", "stream")]
    [InlineData("uri", "stream")]
    [InlineData("ascii_downcase", "closed")]
    [InlineData("ascii_upcase", "closed")]
    [InlineData("html", "closed")]
    [InlineData("uri", "closed")]
    [InlineData("ascii_downcase", "cancelled")]
    [InlineData("ascii_upcase", "cancelled")]
    [InlineData("html", "cancelled")]
    [InlineData("uri", "cancelled")]
    public async Task Jq_TransformsKeepLongStreamsBoundedAndStopAtHostBoundaries(string operation, string mode)
    {
        const int records = 2500;
        const string json = "\"aA<&\\\"é🚀\"\n";
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem { AppendRemainsOpen = mode != "closed" };
        host.AddFile("/input", string.Concat(Enumerable.Repeat(json, records)));
        host.AddFile("/later", json);
        int writes = 0;
        if (mode == "cancelled") host.BeforeByteAppend = () => { if (++writes == 3) cancellation.Cancel(); };
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            operation.StartsWith("ascii_", StringComparison.Ordinal) ? operation : "@" + operation, "/input", "/later")));
        if (mode == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        else Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        string line = operation switch
        {
            "ascii_downcase" => "aa<&\"é🚀\n", "ascii_upcase" => "AA<&\"é🚀\n",
            "html" => "aA&lt;&amp;&quot;é🚀\n", _ => "aA%3C%26%22%C3%A9%F0%9F%9A%80\n"
        };
        Assert.Equal(string.Concat(Enumerable.Repeat(line, mode == "stream" ? records + 1 : mode == "closed" ? 1 : 2)),
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(mode == "stream" ? 2 : 1, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private static string Transform(JqRuntime runtime, string operation, JsonNode input) =>
        operation.StartsWith("ascii_", StringComparison.Ordinal) ? runtime.AsciiCase(input, operation == "ascii_downcase") : runtime.Format(operation, input);
}
