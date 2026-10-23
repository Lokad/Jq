using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("trim", " x ", "x")]
    [InlineData("getpath([{start:1,end:2}])", "abc", "b")]
    [InlineData("index(\"b\")", "abc", "1")]
    [InlineData("[.] | @csv", "a\"b", "\"a\"\"b\"")]
    [InlineData("@urid", "é%C3%A9", "éé")]
    [InlineData("@base64d", "w6k=", "é")]
    [InlineData("tojson", "\ue000", "\"\\ue000\"")]
    public async Task Jq_StringOperationsStreamWithBoundedMemoryAndStopOnClosure(string filter, string input, string output)
    {
        const int records = 2500;
        foreach (bool close in new[] { false, true })
        {
            var root = new BenchmarkMemoryBudget(64 * 1024);
            var host = new MockFileSystem { AppendRemainsOpen = !close };
            host.SetStandardInput(string.Concat(Enumerable.Repeat(input + "\n", records)));
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rr", filter)));
            Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
            Assert.Equal(string.Concat(Enumerable.Repeat(output + "\n", close ? 1 : records)), host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(close ? 1 : records, host.AppendCallCount);
            Assert.Equal(0, root.ReservedBytes);
            Assert.InRange(root.PeakBytes, 1, 64 * 1024);
        }
    }

    [Theory]
    [InlineData("urid", "é%F0%9F%9A%80+%00", "é🚀+\0")]
    [InlineData("base64d", "w6k=ignored", "é")]
    [InlineData("base64d", "w6k", "é")]
    [InlineData("base64d", "/w==", "\ufffd")]
    [InlineData("base64d", "=ignored", "")]
    [InlineData("urid", "", "")]
    [InlineData("base64d", "", "")]
    public void Jq_DecodersPreserveUnicodePaddingAndReplacement(string format, string text, string expected)
    {
        using var budget = new JqBudget(CancellationToken.None);
        Assert.Equal(expected, new JqRuntime(budget).Format(format, JsonValue.Create(text)));
    }

    [Theory]
    [InlineData("urid")]
    [InlineData("base64d")]
    public void Jq_DecodersAdmitOneScratchBufferAndRefundItAfterConversion(string format)
    {
        string expected = new('é', 100000);
        string text = format == "urid" ? expected : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(expected));
        var root = new BenchmarkMemoryBudget(400000);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None))
        {
            Assert.Equal(expected, new JqRuntime(budget).Format(format, JsonValue.Create(text)));
            Assert.Equal(400000, root.PeakBytes);
            Assert.Equal(200000, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("urid")]
    [InlineData("base64d")]
    public void Jq_DecodersCancelBeforeAllocatingAdmittedScratch(string format)
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        JsonNode input = JsonValue.Create(new string('x', 100000));
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<OperationCanceledException>(() => runtime.Format(format, input));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.InRange(allocated, 0, 32 * 1024);
            Assert.Equal(0, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try @urid catch \"caught\"")]
    [InlineData("@urid?")]
    [InlineData("try @base64d catch \"caught\"")]
    [InlineData("@base64d?")]
    public async Task Jq_DecoderRefusalPreservesPrefix(string operation)
    {
        const long limit = 1024 * 1024;
        var root = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.SetStandardInput(new JsonArray(new string('x', 10000)).ToJsonString());
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (.[0] | " + operation + ")")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes - 4096, out pressure));
        };
        Assert.Equal(5, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(null, null, "a🚀bc")]
    [InlineData(1.0, 2.0, "🚀")]
    [InlineData(2.0, 2.0, "")]
    [InlineData(4.0, null, "")]
    [InlineData(1.2, 2.2, "🚀b")]
    [InlineData(-3.0, -1.0, "🚀b")]
    [InlineData(-100.0, 100.0, "a🚀bc")]
    [InlineData(double.NaN, double.NaN, "a🚀bc")]
    public async Task Jq_ScalarSlicesAgreeAcrossReadAndTrackedPaths(double? start, double? end, string expected)
    {
        JsonNode input = JsonValue.Create("a🚀bc");
        using var budget = new JqBudget(CancellationToken.None);
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), budget);
        var filter = new SliceFilter(new IdentityFilter(), new LiteralFilter(start.HasValue ? JsonValue.Create(start.Value) : null),
            new LiteralFilter(end.HasValue ? JsonValue.Create(end.Value) : null), false);
        Assert.Equal(expected, (await filter.Evaluate(input, context, context.RootEnvironment).SingleAsync())?.GetValue<string>());
        Assert.Equal(expected, (await filter.EvaluatePaths(new JqValuePath([], input, true), context, context.RootEnvironment)
            .SingleAsync()).Value?.GetValue<string>());
        Assert.Equal(expected, JqPathReads.GetPath(input, new[] { new SliceSegment(start, end) }, context)?.GetValue<string>());
        var raw = new JsonObject
        {
            ["start"] = start.HasValue ? JsonValue.Create(start.Value) : null,
            ["end"] = end.HasValue ? JsonValue.Create(end.Value) : null
        };
        Assert.Equal(expected, JqPathReads.GetPath(input, new[] { new SliceObjectSegment(raw) }, context)?.GetValue<string>());
    }

    [Fact]
    public async Task Jq_PathSliceAdmitsSelectedSubstringAndBorrowsFullSlice()
    {
        string text = new('x', 100000);
        JsonNode input = JsonValue.Create(text);
        var root = new BenchmarkMemoryBudget(4096);
        using (var cancellation = new CancellationTokenSource())
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token))
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), budget))
        {
            Assert.Same(text, context.Runtime.SliceString(text, null, null));
            Assert.Equal(0, root.ReservedBytes);
            Assert.Equal("x", JqPathReads.GetPath(input, new[] { new SliceSegment(1, 2) }, context)?.GetValue<string>());
            Assert.Equal(2, root.ReservedBytes);
            var filter = new SliceFilter(new IdentityFilter(), new LiteralFilter(JsonValue.Create(1)), new LiteralFilter(JsonValue.Create(2)), false);
            var path = await filter.EvaluatePaths(new JqValuePath([], input, true), context, context.RootEnvironment).SingleAsync();
            Assert.True(path.Tracked);
            Assert.Equal("x", path.Value?.GetValue<string>());
            Assert.Single(path.Segments);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => context.Runtime.Length(input));
            Assert.Throws<OperationCanceledException>(() => context.Runtime.SliceString(text, 1, 2));
        }
        Assert.Equal(0, root.ReservedBytes);
    }

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
    [InlineData("", false)]
    [InlineData("x", false)]
    [InlineData("xxxxxxxxxxxxxxxz", false)]
    [InlineData("", true)]
    [InlineData("x", true)]
    [InlineData("xxxxxxxxxxxxxxxz", true)]
    public void Jq_StringIndexBorrowsInputWithoutSearchTables(string needle, bool last)
    {
        JsonNode input = JsonValue.Create(new string('x', 100000));
        var root = new BenchmarkMemoryBudget(0);
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token);
        var runtime = new JqRuntime(budget);
        JsonNode pattern = JsonValue.Create(needle);
        JsonNode? result = runtime.Index(input, pattern, last);
        if (needle == "x") Assert.Equal(last ? 99999 : 0, result?.GetValue<int>());
        else Assert.Null(result);
        Assert.Equal(0, root.ReservedBytes);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => runtime.Index(input, pattern, last));
    }

    [Fact]
    public async Task Jq_DenseLastIndexKeepsLongInputStreamsBounded()
    {
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        string record = new JsonArray(new string('a', 100000)).ToJsonString();
        host.AddFile("/input", string.Concat(Enumerable.Repeat(record + "\n", 20)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".[0] | rindex(\"a\")", "/input")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("99999\n", 20)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
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
