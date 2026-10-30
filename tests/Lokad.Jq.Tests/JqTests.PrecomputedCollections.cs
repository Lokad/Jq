using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("_sort_by_impl", "[1,1,0] | debug", "[\"c\",\"a\",\"b\"]\n", "[\"DEBUG:\",[1,1,0]]\n")]
    [InlineData("_group_by_impl", "[1,1,0] | debug", "[[\"c\"],[\"a\",\"b\"]]\n", "[\"DEBUG:\",[1,1,0]]\n")]
    [InlineData("_unique_by_impl", "[1,1,0] | debug", "[\"c\",\"a\"]\n", "[\"DEBUG:\",[1,1,0]]\n")]
    // Retain the documented stable managed order for NaN keys.
    [InlineData("_sort_by_impl", "[nan,nan,nan]", "[\"a\",\"b\",\"c\"]\n", "")]
    [InlineData("_group_by_impl", "[nan,nan,nan]", "[[\"a\"],[\"b\"],[\"c\"]]\n", "")]
    [InlineData("_unique_by_impl", "[nan,nan,nan]", "[\"a\",\"b\",\"c\"]\n", "")]
    public async Task Jq_PrecomputedKeysKeepStableTiesAndEffects(string operation, string keys, string expected, string error)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"a\",\"b\",\"c\"]\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", operation + "(" + keys + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("_sort_by_impl", 100000)]
    [InlineData("_group_by_impl", 1)]
    [InlineData("_unique_by_impl", 1)]
    public async Task Jq_PrecomputedKeysDoNotCopyPairInventories(string operation, int count)
    {
        var source = new JqArray();
        for (int index = 0; index < 100000; index++) source.Add((JqValue?)null);
        var root = new BenchmarkMemoryBudget(6 * 1024 * 1024);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None)))
        {
            JqFilter filter = operation switch
            {
                "_sort_by_impl" => new SortByImplFilter(new IdentityFilter()),
                "_group_by_impl" => new GroupByImplFilter(new IdentityFilter()),
                _ => new UniqueByImplFilter(new IdentityFilter())
            };
            var output = Assert.IsType<JqArray>(await filter.Evaluate(source, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(count, output.Count);
            if (operation == "_group_by_impl") Assert.Equal(100000, Assert.IsType<JqArray>(output[0]).Count);
            Assert.Equal(100000, source.Count);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("_sort_by_impl")]
    [InlineData("_group_by_impl")]
    [InlineData("_unique_by_impl")]
    public async Task Jq_PrecomputedKeysConstructIndependentOutputs(string operation)
    {
        var source = new JqArray(new JqArray(2), new JqArray(1));
        using var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None));
        JqFilter filter = operation switch
        {
            "_sort_by_impl" => new SortByImplFilter(new IdentityFilter()),
            "_group_by_impl" => new GroupByImplFilter(new IdentityFilter()),
            _ => new UniqueByImplFilter(new IdentityFilter())
        };
        var output = Assert.IsType<JqArray>(await filter.Evaluate(source, context, context.RootEnvironment).SingleAsync());
        var first = Assert.IsType<JqArray>(operation == "_group_by_impl" ? output[0]?[0] : output[0]);
        Assert.NotSame(source[1], first);
        first[0] = 9;
        Assert.Equal(1, source[1]?[0]?.GetValue<int>());
    }

    [Theory]
    [InlineData("_sort_by_impl")]
    [InlineData("_group_by_impl")]
    [InlineData("_unique_by_impl")]
    public async Task Jq_PrecomputedKeysPropagateCancellationFromSortComparison(string operation)
    {
        var source = new JqArray(new JqObject(NativeFixtures.Budget()) { ["a"] = 2 }, new JqObject(NativeFixtures.Budget()) { ["a"] = 1 });
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        int admissions = 0;
        var memory = new AdmissionCallbackBudget(root, () => { if (++admissions == 2) cancellation.Cancel(); });
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token)))
        {
            JqFilter filter = operation switch
            {
                "_sort_by_impl" => new SortByImplFilter(new IdentityFilter()),
                "_group_by_impl" => new GroupByImplFilter(new IdentityFilter()),
                _ => new UniqueByImplFilter(new IdentityFilter())
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await filter.Evaluate(source, context, context.RootEnvironment).ToArrayAsync());
            Assert.Equal(2, admissions);
        }
        Assert.Equal(0, root.ReservedBytes);
    }
}
