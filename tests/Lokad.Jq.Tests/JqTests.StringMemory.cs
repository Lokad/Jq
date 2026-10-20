using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("", "", "", "")]
    [InlineData(" \t\r\n", "", "", "")]
    [InlineData("\u2000🚀\u3000", "🚀", "🚀\u3000", "\u2000🚀")]
    [InlineData("\u200bx\u200b", "\u200bx\u200b", "\u200bx\u200b", "\u200bx\u200b")]
    [InlineData("\u0085\u00a0x\u2028\u202f", "x", "x\u2028\u202f", "\u0085\u00a0x")]
    public void Jq_TrimPreservesWhitespaceAndSupplementaryCharacters(string text, string both, string left, string right)
    {
        using var budget = new JqBudget(CancellationToken.None);
        var runtime = new JqRuntime(budget);
        JsonNode input = JsonValue.Create(text);
        Assert.Equal(both, runtime.TrimSides(input, true, true));
        Assert.Equal(left, runtime.TrimSides(input, true, false));
        Assert.Equal(right, runtime.TrimSides(input, false, true));
    }

    [Fact]
    public void Jq_TrimBorrowsUnchangedInputWithoutAdmission()
    {
        string text = new('x', 100000);
        var root = new BenchmarkMemoryBudget(0);
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token);
        var runtime = new JqRuntime(budget);
        JsonNode input = JsonValue.Create(text);
        Assert.Same(text, runtime.TrimSides(input, true, true));
        Assert.Equal(0, root.ReservedBytes);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => runtime.TrimSides(input, true, true));
    }

    [Fact]
    public void Jq_TrimAdmitsOnlyTheResultSubstring()
    {
        var root = new BenchmarkMemoryBudget(2);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            Assert.Equal("x", runtime.TrimSides(JsonValue.Create(new string(' ', 100000) + "x"), true, true));
            Assert.Equal(2, root.ReservedBytes);
            Assert.True(Assert.Throws<JqQuotaException>(() => runtime.TrimSides(JsonValue.Create(" xy "), true, true)).IsMemoryAdmission);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[1,1,1,1]", "indices([1,1])", "[0,1,2]\n")]
    [InlineData("[1,1,1]", "rindex([1,1])", "1\n")]
    [InlineData("[1,1,1,1]", "getpath([[1,1]])", "[0,1,2]\n")]
    [InlineData("[1,2,1,2,1]", "indices([1,2,1])", "[0,2]\n")]
    [InlineData("[1,2,1,2]", "indices([1,2])", "[0,2]\n")]
    [InlineData("[1,1,1]", "index([1,1])", "0\n")]
    [InlineData("[null,null,null]", "indices([null,null])", "[0,1]\n")]
    [InlineData("[[1],[1],[1]]", "indices([[1],[1]])", "[0,1]\n")]
    [InlineData("[1,2]", "[indices([]),indices([1,2,3]),indices([nan])]", "[[],[],[]]\n")]
    [InlineData("\"a🚀🚀z\"", "[indices(\"🚀\"),index(\"z\"),rindex(\"🚀\")]", "[[1,2],3,2]\n")]
    [InlineData("\"aaaa\"", "indices(\"aa\")", "[0,1,2]\n")]
    [InlineData("\"abc\"", "[indices(\"\"),index(\"\"),index(\"abcd\")]", "[[],null,null]\n")]
    public async Task Jq_SearchPreservesOverlapsAndScalarOffsets(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("xxxxxxxxxxxxxxxz")]
    public void Jq_StringIndexBorrowsInputWithoutSearchTables(string needle)
    {
        JsonNode input = JsonValue.Create(new string('x', 100000));
        var root = new BenchmarkMemoryBudget(0);
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token);
        var runtime = new JqRuntime(budget);
        JsonNode pattern = JsonValue.Create(needle);
        JsonNode? result = runtime.Index(input, pattern);
        if (needle == "x") Assert.Equal(0, result?.GetValue<int>());
        else Assert.Null(result);
        Assert.Equal(0, root.ReservedBytes);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => runtime.Index(input, pattern));
    }

    [Fact]
    public async Task Jq_RepetitiveUnsuccessfulSearchObservesCancellation()
    {
        JsonNode input = JsonValue.Create(new string('x', 20000));
        JsonNode pattern = JsonValue.Create(new string('x', 9999) + "z");
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, null, cancellation.Token);
        var runtime = new JqRuntime(budget);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = Task.Run(() =>
        {
            started.SetResult();
            // Finite repetitive work lets cancellation race any unsuccessful scan,
            // including a scan started after the cancellation request.
            for (int index = 0; index < 100; index++) Assert.Null(runtime.Index(input, pattern));
        });
        await started.Task;
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Theory]
    [InlineData("try @csv catch \"caught\"")]
    [InlineData("@csv?")]
    public async Task Jq_CsvExpansionRefusalPreservesPrefixAndCleanup(string operation)
    {
        const long limit = 1024 * 1024;
        var root = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.AddFile("/input", new JsonArray(new string('"', 10000)).ToJsonString());
        host.AddFile("/later", "[]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"ready\", (" + operation + ")", "/input", "/later")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes - 4096, out pressure));
        };
        Assert.Equal(5, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_CsvRefusalDoesNotAllocateEscapedCopy()
    {
        JsonNode input = new JsonArray(new string('"', 100000));
        var root = new BenchmarkMemoryBudget(2);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        for (int index = 0; index < 2; index++)
            Assert.True(Assert.Throws<JqQuotaException>(() => runtime.Format("csv", input)).IsMemoryAdmission);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var failure = Assert.Throws<JqQuotaException>(() => runtime.Format("csv", input));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(failure.IsMemoryAdmission);
        Assert.InRange(allocated, 0, 32 * 1024);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[]", "\n")]
    [InlineData("[null,\"\",true,false,1.5]", ",\"\",true,false,1.5\n")]
    [InlineData("[\"a,b\",\"c\\\"d\",\"é🚀\",\"\\n\"]", "\"a,b\",\"c\"\"d\",\"é🚀\",\"\n\"\n")]
    public async Task Jq_CsvExpansionPreservesExactFields(string input, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "@csv")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
