using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
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
    public void Jq_TransposeNullSlotsPreflightBeforeMaterialization()
    {
        var first = new JsonArray();
        for (int index = 0; index < 512; index++) first.Add((JsonNode?)null);
        var input = new JsonArray(first);
        for (int index = 1; index < 512; index++) input.Add((JsonNode?)null);
        var root = new BenchmarkMemoryBudget(0);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None);
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), budget);
        var filter = new TransposeFilter();
        var failure = Assert.Throws<JqQuotaException>(() => filter.Evaluate(input, context, context.RootEnvironment).Single());
        Assert.True(failure.IsMemoryAdmission);
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(512, input.Count);
    }

    [Fact]
    public void Jq_TransposeBorrowsRowsAndOwnsCopiedCells()
    {
        var input = new JsonArray();
        for (int index = 0; index < 100000; index++) input.Add((JsonNode?)null);
        var root = new BenchmarkMemoryBudget(256);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None))
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), budget))
        {
            Assert.Empty(Assert.IsType<JsonArray>(new TransposeFilter().Evaluate(input, context, context.RootEnvironment).Single()));
            Assert.Equal(64, root.PeakBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
        JsonNode original = new JsonArray(new JsonArray(new JsonObject { ["x"] = 1 }));
        using var copying = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(CancellationToken.None));
        var result = Assert.IsType<JsonArray>(new TransposeFilter().Evaluate(original, copying, copying.RootEnvironment).Single());
        result[0]?[0]?["x"] = 2;
        Assert.Equal(1, original[0]?[0]?["x"]?.GetValue<int>());
    }

    [Fact]
    public void Jq_TransposePreservesCancellationAndConstructionDepth()
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token))
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), budget))
            Assert.Throws<OperationCanceledException>(() => new TransposeFilter()
                .Evaluate(new JsonArray(new JsonArray(1)), context, context.RootEnvironment).Single());
        Assert.Equal(0, root.ReservedBytes);
        JsonNode value = JsonValue.Create(1);
        for (int depth = 0; depth < 62; depth++) value = new JsonArray(value);
        var input = new JsonArray(new JsonArray(value));
        using var deep = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(CancellationToken.None));
        Assert.IsType<JsonArray>(new TransposeFilter().Evaluate(input, deep, deep.RootEnvironment).Single());
    }
}
