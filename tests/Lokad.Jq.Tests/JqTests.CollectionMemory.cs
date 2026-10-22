using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("transpose | map(length)", "[[1,2],null,[3]]", "[3,3]\n")]
    [InlineData("JOIN(null; \"missing\") | length", "[null,null]", "2\n")]
    [InlineData("JOIN(null; .[]; \"missing\") | .[0]", "[null,null]", "null\nnull\n")]
    [InlineData("JOIN(null; .[]; \"missing\"; .[1])", "[null,null]", "null\nnull\n")]
    [InlineData("getpath([\"a\"]) | length", "{\"a\":[1,2,3]}", "3\n")]
    [InlineData(".a = .b | .a | length", "{\"a\":[],\"b\":[1,2,3]}", "3\n")]
    [InlineData(".left + .right | .keep", "{\"left\":{\"drop\":[1,2],\"keep\":1},\"right\":{\"drop\":0}}", "1\n")]
    [InlineData(".left * .right | .child | keys_unsorted", "{\"left\":{\"child\":{\"a\":1}},\"right\":{\"child\":{\"b\":2}}}", "[\"a\",\"b\"]\n")]
    [InlineData("[range(0;3;1)]", "null", "[0,1,2]\n")]
    [InlineData("arrays | values | abs", "[1,2]", "[1,2]\n")]
    [InlineData("add", "[[1],[2],[3]]", "[1,2,3]\n")]
    [InlineData("walk(.)", "{\"a\":[1,2]}", "{\"a\":[1,2]}\n")]
    [InlineData("with_entries(.value += 1)", "{\"a\":1,\"b\":2}", "{\"a\":2,\"b\":3}\n")]
    [InlineData("limit(3; while(.<3; .+1))", "0", "0\n1\n2\n")]
    [InlineData("until(. == 3; .+1)", "0", "3\n")]
    [InlineData("limit(3; recurse(.+1))", "0", "0\n1\n2\n")]
    public async Task Jq_CollectionOperationsKeepLongRecordStreamsBoundedAndStopOnClosure(string filter, string input, string expected)
    {
        const int records = 2500;
        foreach (bool close in new[] { false, true })
        {
            var root = new BenchmarkMemoryBudget(64 * 1024);
            var host = new MockFileSystem { AppendRemainsOpen = !close };
            host.SetStandardInput(string.Concat(Enumerable.Repeat(input + "\n", records)));
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
            Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
            Assert.Equal(close ? expected[..(expected.IndexOf('\n') + 1)] : string.Concat(Enumerable.Repeat(expected, records)),
                host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(close ? 1 : records * expected.Count(character => character == '\n'), host.AppendCallCount);
            Assert.Equal(0, root.ReservedBytes);
            Assert.InRange(root.PeakBytes, 1, 64 * 1024);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_JoinWaitsForPendingOutputAndCleanupBeforeRefunding(bool cancel)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem();
        inner.AddFile("/input", "[null,null]");
        var host = new MemoryBorrowHost(inner) { DelayCleanup = true };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "JOIN(null; .[]; \"missing\")", "/input")));
        Task<int> execution = command.ExecuteAsync(host, root, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        byte[] first = "[null,null]\n"u8.ToArray();
        long charged = root.ReservedBytes;
        Assert.Equal(first, pending.ToArray());
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, root.ReservedBytes);
        Assert.Equal(first, pending.ToArray());
        host.Release.SetResult();
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(execution.IsCompleted);
        Assert.True(root.ReservedBytes > 0);
        host.CloseRelease.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else
        {
            Assert.Equal(0, await execution);
            Assert.Equal("[null,null]\n[null,null]\n", inner.GetOutput(JqFileDescriptor.StdOut));
        }
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try (JOIN(null; \"missing\") | length) catch \"caught\"")]
    [InlineData("(JOIN(null; \"missing\") | length)?")]
    [InlineData("try (JOIN(null; .; \"missing\") | length) catch \"caught\"")]
    [InlineData("(JOIN(null; .; \"missing\") | length)?")]
    [InlineData("try JOIN(null; .; \"missing\"; length) catch \"caught\"")]
    [InlineData("JOIN(null; .; \"missing\"; length)?")]
    public async Task Jq_JoinConstructionAdmissionPreservesPrefixAndCleanup(string operation)
    {
        const long limit = 4 * 1024 * 1024;
        string input = "[" + string.Join(',', Enumerable.Repeat("null", 10000)) + "]";
        var root = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.AddFile("/input", input);
        host.AddFile("/later", input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (" + operation + ")", "/input", "/later")));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_JoinNullPairsNeedAdmissionAndStillCancel(bool stream)
    {
        JsonNode input = new JsonArray((JsonNode?)null);
        JqFilter filter = new SqlJoinFilter(new LiteralFilter(null), stream ? new IdentityFilter() : null,
            new LiteralFilter(JsonValue.Create("missing")), null);
        var root = new BenchmarkMemoryBudget(0);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
            Assert.True((await Assert.ThrowsAsync<JqQuotaException>(async () => await filter.Evaluate(input, context, context.RootEnvironment).FirstAsync())).IsMemoryAdmission);
        using var cancellation = new CancellationTokenSource();
        var admitted = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new AdmissionCallbackBudget(admitted, cancellation.Cancel);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token)))
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await filter.Evaluate(input, context, context.RootEnvironment).FirstAsync());
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, admitted.ReservedBytes);
    }

    [Fact]
    public async Task Jq_JoinCopiesBorrowedLookupsOnceAndKeepsPairsIndependent()
    {
        var lookup = new JsonArray();
        for (int index = 0; index < 1000; index++) lookup.Add(index);
        JsonNode idx = new JsonObject { ["k"] = lookup };
        var root = new BenchmarkMemoryBudget(100000);
        var variables = new Dictionary<string, JsonNode?> { ["idx"] = idx };
        using (var context = new JqContext(variables, new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            var filter = new SqlJoinFilter(new VariableFilter("idx"), new LiteralFilter(null), new LiteralFilter(JsonValue.Create("k")), null);
            var result = Assert.IsType<JsonArray>(await filter.Evaluate(null, context, context.RootEnvironment).SingleAsync());
            Assert.Null(result[0]);
            var copied = Assert.IsType<JsonArray>(result[1]);
            Assert.Equal(1000, copied.Count);
            copied[0] = -1;
            Assert.Equal(0, lookup[0]?.GetValue<int>());
            Assert.InRange(root.PeakBytes, 88000, 100000);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[null,null]", "JOIN(null; \"missing\")", "[[null,null],[null,null]]\n")]
    [InlineData("[1,2]", "JOIN({\"1\":{a:1}}; tostring)", "[[1,{\"a\":1}],[2,null]]\n")]
    [InlineData("[1,2]", "JOIN({\"1\":10}; .[]; tostring)", "[1,10]\n[2,null]\n")]
    [InlineData("[1,2]", "JOIN({\"1\":10}; .[]; tostring; .[1])", "10\nnull\n")]
    [InlineData("[1,2]", "JOIN(null; empty)", "[[1],[2]]\n")]
    [InlineData("[1]", "JOIN(null; (\"a\",\"b\"))", "[[1,null,null]]\n")]
    [InlineData("[1,2]", "JOIN(null; .[]; empty)", "[1]\n[2]\n")]
    [InlineData("[1,2]", "JOIN(null; .[]; empty; length)", "1\n1\n")]
    [InlineData("[1,2]", "JOIN({a:3,b:4}; (\"a\",\"b\"))", "[[1,3,4],[2,3,4]]\n")]
    [InlineData("[1,2]", "JOIN({a:3,b:4}; .[]; (\"a\",\"b\"))", "[1,3,4]\n[2,3,4]\n")]
    [InlineData("[1,2]", "JOIN({a:3,b:4}; .[]; (\"a\",\"b\"); add)", "8\n9\n")]
    [InlineData("null", "try first(JOIN(null; null; (\"a\",error(\"late\")))) catch .", "\"late\"\n")]
    [InlineData("null", "try first(JOIN(null; null; (\"a\",error(\"late\")); .)) catch .", "\"late\"\n")]
    public async Task Jq_JoinPreservesCollectionAndStreamingOutputs(string input, string filter, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_JoinCompletesEachRowsKeysBeforeEmittingItsPair()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-cn",
            "[JOIN(null; (1,2); ((\"a\",\"b\") | debug)) | debug]")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("[[1,null,null],[2,null,null]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",\"a\"]\n[\"DEBUG:\",\"b\"]\n[\"DEBUG:\",[1,null,null]]\n"
            + "[\"DEBUG:\",\"a\"]\n[\"DEBUG:\",\"b\"]\n[\"DEBUG:\",[2,null,null]]\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try (transpose | length) catch \"caught\"")]
    [InlineData("(transpose | length)?")]
    public async Task Jq_TransposeAdmissionPreservesPrefixAndCleanup(string operation)
    {
        const long limit = 4 * 1024 * 1024;
        const int width = 256;
        string input = "[[" + string.Join(',', Enumerable.Repeat("null", width)) + "],"
            + string.Join(',', Enumerable.Repeat("null", width - 1)) + "]";
        var root = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.AddFile("/input", input);
        host.AddFile("/later", input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (" + operation + ")", "/input", "/later")));
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

    [Theory]
    [InlineData("[[1,2],null,[3]]", "[[1,null,3],[2,null,null]]\n")]
    [InlineData("{\"z\":[1,2],\"a\":[3]}", "[[1,3],[2,null]]\n")]
    [InlineData("[null,null]", "[]\n")]
    [InlineData("[]", "[]\n")]
    [InlineData("{}", "[]\n")]
    public async Task Jq_TransposeAdmitsColumnsAndKeepsRaggedRowOrder(string input, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "transpose")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_TransposeNullSlotsPreflightBeforeMaterialization()
    {
        var first = new JsonArray();
        for (int index = 0; index < 512; index++) first.Add((JsonNode?)null);
        var input = new JsonArray(first);
        for (int index = 1; index < 512; index++) input.Add((JsonNode?)null);
        var root = new BenchmarkMemoryBudget(0);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None);
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), budget);
        var filter = new TransposeFilter();
        var failure = await Assert.ThrowsAsync<JqQuotaException>(async () => await filter.Evaluate(input, context, context.RootEnvironment).SingleAsync());
        Assert.True(failure.IsMemoryAdmission);
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(512, input.Count);
    }

    [Fact]
    public async Task Jq_TransposeBorrowsRowsAndOwnsCopiedCells()
    {
        var input = new JsonArray();
        for (int index = 0; index < 100000; index++) input.Add((JsonNode?)null);
        var root = new BenchmarkMemoryBudget(256);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None))
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), budget))
        {
            Assert.Empty(Assert.IsType<JsonArray>(await new TransposeFilter().Evaluate(input, context, context.RootEnvironment).SingleAsync()));
            Assert.Equal(64, root.PeakBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
        JsonNode original = new JsonArray(new JsonArray(new JsonObject { ["x"] = 1 }));
        using var copying = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(CancellationToken.None));
        var result = Assert.IsType<JsonArray>(await new TransposeFilter().Evaluate(original, copying, copying.RootEnvironment).SingleAsync());
        result[0]?[0]?["x"] = 2;
        Assert.Equal(1, original[0]?[0]?["x"]?.GetValue<int>());
    }

    [Fact]
    public async Task Jq_TransposePreservesCancellationAndConstructionDepth()
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token))
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), budget))
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await new TransposeFilter()
                .Evaluate(new JsonArray(new JsonArray(1)), context, context.RootEnvironment).SingleAsync());
        Assert.Equal(0, root.ReservedBytes);
        JsonNode value = JsonValue.Create(1);
        for (int depth = 0; depth < 62; depth++) value = new JsonArray(value);
        var input = new JsonArray(new JsonArray(value));
        using var deep = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(CancellationToken.None));
        Assert.IsType<JsonArray>(await new TransposeFilter().Evaluate(input, deep, deep.RootEnvironment).SingleAsync());
    }
}
