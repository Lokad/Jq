using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("_sort_by_impl", "[\"c\",\"a\",\"b\"]\n")]
    [InlineData("_group_by_impl", "[[\"c\"],[\"a\",\"b\"]]\n")]
    [InlineData("_unique_by_impl", "[\"c\",\"a\"]\n")]
    public async Task Jq_PrecomputedKeysKeepStableTiesAndEffects(string operation, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"a\",\"b\",\"c\"]\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", operation + "([1,1,0] | debug)")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",[1,1,0]]\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("_sort_by_impl", 100000)]
    [InlineData("_group_by_impl", 1)]
    [InlineData("_unique_by_impl", 1)]
    public void Jq_PrecomputedKeysDoNotCopyPairInventories(string operation, int count)
    {
        var source = new JsonArray();
        for (int index = 0; index < 100000; index++) source.Add((JsonNode?)null);
        var root = new BenchmarkMemoryBudget(6 * 1024 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            JqFilter filter = operation switch
            {
                "_sort_by_impl" => new SortByImplFilter(new IdentityFilter()),
                "_group_by_impl" => new GroupByImplFilter(new IdentityFilter()),
                _ => new UniqueByImplFilter(new IdentityFilter())
            };
            var output = Assert.IsType<JsonArray>(filter.Evaluate(source, context, context.RootEnvironment).Single());
            Assert.Equal(count, output.Count);
            if (operation == "_group_by_impl") Assert.Equal(100000, Assert.IsType<JsonArray>(output[0]).Count);
            Assert.Equal(100000, source.Count);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("_sort_by_impl")]
    [InlineData("_group_by_impl")]
    [InlineData("_unique_by_impl")]
    public void Jq_PrecomputedKeysConstructIndependentOutputs(string operation)
    {
        var source = new JsonArray(new JsonArray(2), new JsonArray(1));
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(CancellationToken.None));
        JqFilter filter = operation switch
        {
            "_sort_by_impl" => new SortByImplFilter(new IdentityFilter()),
            "_group_by_impl" => new GroupByImplFilter(new IdentityFilter()),
            _ => new UniqueByImplFilter(new IdentityFilter())
        };
        var output = Assert.IsType<JsonArray>(filter.Evaluate(source, context, context.RootEnvironment).Single());
        var first = Assert.IsType<JsonArray>(operation == "_group_by_impl" ? output[0]?[0] : output[0]);
        Assert.NotSame(source[1], first);
        first[0] = 9;
        Assert.Equal(1, source[1]?[0]?.GetValue<int>());
        Assert.Same(source, source[1]?.Parent);
    }

    [Theory]
    [InlineData("_sort_by_impl")]
    [InlineData("_group_by_impl")]
    [InlineData("_unique_by_impl")]
    public void Jq_PrecomputedKeysPropagateCancellationFromSortComparison(string operation)
    {
        var source = new JsonArray(new JsonObject { ["a"] = 2 }, new JsonObject { ["a"] = 1 });
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        int admissions = 0;
        var memory = new AdmissionCallbackBudget(root, () => { if (++admissions == 2) cancellation.Cancel(); });
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token)))
        {
            JqFilter filter = operation switch
            {
                "_sort_by_impl" => new SortByImplFilter(new IdentityFilter()),
                "_group_by_impl" => new GroupByImplFilter(new IdentityFilter()),
                _ => new UniqueByImplFilter(new IdentityFilter())
            };
            Assert.ThrowsAny<OperationCanceledException>(() => filter.Evaluate(source, context, context.RootEnvironment).ToArray());
            Assert.Equal(2, admissions);
        }
        Assert.Equal(0, root.ReservedBytes);
    }
}
