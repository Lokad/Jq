using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ExtremaKeyScratchDoesNotGrowWithInputLength(bool maximum)
    {
        var source = new JqArray();
        for (int index = 0; index < 100000; index++) source.Add((JqValue?)null);
        var root = new BenchmarkMemoryBudget(4096);
        var counted = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(),
            new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None)))
        {
            var filter = new MinMaxByFilter(new IdentityFilter(), maximum);
            Assert.Null(await filter.Evaluate(source, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(0, root.ReservedBytes);
            Assert.InRange(root.PeakBytes, 128, 256);
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 3);
            Assert.Equal(0, counted.Counts.BytesReads);
            Assert.Equal(0, counted.Counts.SameSizeResizes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task Jq_ExtremaKeepBorrowedTieIdentityAndAllKeyOutputs(bool maximum, int selected)
    {
        var source = new JqArray(new JqArray(1), new JqArray(1), new JqArray(1));
        var root = new BenchmarkMemoryBudget(4096);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(),
            new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None)))
        {
            var parser = new JqParser(maximum ? "max_by((.,.))" : "min_by((.,.))", JqProgramSource.Inline,
                context.RootEnvironment, context.Budget);
            Assert.Same(source[selected], await parser.Parse().Evaluate(source, context, context.RootEnvironment).SingleAsync());
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ExtremaCancelDuringKeyCollectionAndRefundScratch(bool maximum)
    {
        var root = new BenchmarkMemoryBudget(4096);
        using var cancellation = new CancellationTokenSource();
        using (var context = new JqContext(new Dictionary<string, JqValue?>(),
            new JqBudget(Utf8RegexAdapter.Instance, root, cancellation.Token)))
        {
            var filter = new MinMaxByFilter(new CancellingKeys(cancellation), maximum);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await filter.Evaluate(new JqArray(1, 2), context, context.RootEnvironment).ToArrayAsync());
            Assert.Equal(0, root.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("try min_by(range(10000)) catch \"caught\"")]
    [InlineData("max_by(range(10000))?")]
    public async Task Jq_ExtremaKeyScratchRefusalIsTerminal(string source)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "[1,2]\n");
        host.AddFile("/later", "invalid JSON");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "\"prefix\", (" + source + ")", "/input", "/later")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("\"prefix\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    private sealed class CancellingKeys(CancellationTokenSource cancellation) : JqFilter
    {
        protected override async IAsyncEnumerable<JqValue?> EvaluateCore(JqValue? input, JqContext context, JqEnvironment environment)
        {
            yield return input;
            cancellation.Cancel();
            yield return input;
        }
    }
}
