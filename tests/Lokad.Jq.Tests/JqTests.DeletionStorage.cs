using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    // jq 1.8.2: nested deletion acts on the slice, then writes it back.
    [Theory]
    [InlineData("del(.[0:1], getpath([{start:2,end:3}]))", "[1,3]\n")]
    [InlineData("del(getpath([{start:2,end:3}]), .[0:1])", "[1,3]\n")]
    [InlineData("del(.[0:2][0])", "[1,2,3]\n")]
    [InlineData("del(.[0:2][1])", "[0,2,3]\n")]
    [InlineData("del(.[0:2][])", "[2,3]\n")]
    [InlineData("delpaths([[{start:0,end:1},0],[{start:2,end:3},0]])", "[1,2]\n")]
    [InlineData("delpaths([[{start:0,end:2}],[{start:1,end:3}]])", "[3]\n")]
    [InlineData("delpaths([[{start:infinite,end:infinite}],[{start:0,end:1}]])", "[1,2,3]\n")]
    [InlineData("try delpaths([[{start:infinite,end:infinite}],[{start:0,end:1}]]) catch .", "[1,2,3]\n")]
    [InlineData("delpaths([[{start:0,end:1,unused:infinite}],[{start:2,end:3}]])", "[1,3]\n")]
    [InlineData("delpaths([[{start:nan,end:1}],[{start:0,end:1}]])", "[1,2,3]\n")]
    public async Task Jq_DeletionPreservesMixedNestedAndNonfiniteSlices(string source, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[0,1,2,3]\n");
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_DeletionRefusesBeforeBuildingDeepAncestorState()
    {
        JqValue value = new JqArray((JqValue?)null);
        for (int index = 1; index < 5000; index++) value = new JqArray(value);
        IReadOnlyList<JqValueSegment> path = Enumerable.Range(0, 5000).Select(_ => (JqValueSegment)new IndexSegment(0, false)).ToArray();
        var root = new BenchmarkMemoryBudget(0);
        var counted = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None)))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(Assert.Throws<JqQuotaException>(() => JqPathDeletes.DeletePaths(value, new[] { path }, context)).IsMemoryAdmission);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
            Assert.Equal(1, counted.Counts.Refusals);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_DeletionCancelsAfterScratchAdmissionAndRefunds()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token)))
            Assert.ThrowsAny<OperationCanceledException>(() => JqPathDeletes.DeletePaths(new JqArray(0, 1),
                new IReadOnlyList<JqValueSegment>[] { new JqValueSegment[] { new IndexSegment(0, false) }, new JqValueSegment[] { new IndexSegment(1, false) } }, context));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try delpaths($paths) catch \"caught\"")]
    [InlineData("delpaths($paths)?")]
    public async Task Jq_DeletionScratchRefusalStaysTerminalAndClosesOwnedInput(string source)
    {
        const long allowance = 8 * 1024 * 1024;
        var root = new BenchmarkMemoryBudget(allowance);
        string paths = "[" + string.Join(',', Enumerable.Range(0, 1000).Select(x => "[" + x + "]")) + "]";
        var host = new MockFileSystem();
        host.AddFile("/input", "[0,1]\n");
        host.AddFile("/later", "invalid JSON");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--argjson", "paths", paths,
            "\"prefix\", (" + source + ")", "/input", "/later")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(allowance - root.ReservedBytes - 80 * 1024, out pressure));
        };
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("\"prefix\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("getpath($path)", "[0]\n")]
    [InlineData("setpath($path; [9])", "[9,1,2,3]\n")]
    [InlineData("delpaths($paths)", "[1,2,3]\n")]
    public async Task Jq_PathOperationsIgnoreDeepUnusedSliceFields(string operation, string expected)
    {
        bool deletion = operation.StartsWith("delpaths", StringComparison.Ordinal);
        string setup = deletion
            ? "([[{start:0,end:1}]] | setpath([0,0,\"unused\"] + [range(100)|0];null)) as $paths"
            : "([{start:0,end:1}] | setpath([0,\"unused\"] + [range(100)|0];null)) as $path";
        var host = new MockFileSystem();
        var root = new BenchmarkMemoryBudget(512 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", setup + " | [0,1,2,3] | " + operation)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_PathParsingBorrowsIgnoredSliceFieldsWithoutTraversal()
    {
        var payload = new JqArray();
        for (int index = 0; index < 100000; index++) payload.Add(index);
        var slice = new JqObject(NativeFixtures.Budget()) { ["start"] = 0, ["end"] = 1, ["unused"] = payload };
        var root = new BenchmarkMemoryBudget(128);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None)))
        {
            var paths = JqPaths.ParsePathValue(new JqArray(slice), context);
            Assert.Same(slice, Assert.IsType<SliceObjectSegment>(Assert.Single(paths)).Raw);
            Assert.Equal(64, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }
}
