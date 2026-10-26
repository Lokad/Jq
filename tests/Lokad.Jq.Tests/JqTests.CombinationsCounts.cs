using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("empty", "[[]]\n")]
    [InlineData("(1,2)|select(.>10)", "[[]]\n")]
    [InlineData("(0,0)", "[[]]\n")]
    [InlineData("(0,empty)", "[[]]\n")]
    [InlineData("(0,1)", "[[0],[1]]\n")]
    [InlineData("(1,2)", "[[0,0,0],[0,0,1],[0,1,0],[0,1,1],[1,0,0],[1,0,1],[1,1,0],[1,1,1]]\n")]
    [InlineData("(1,1,1)", "[[0,0,0],[0,0,1],[0,1,0],[0,1,1],[1,0,0],[1,0,1],[1,1,0],[1,1,1]]\n")]
    [InlineData("(0,2.1)", "[[0,0,0],[0,0,1],[0,1,0],[0,1,1],[1,0,0],[1,0,1],[1,1,0],[1,1,1]]\n")]
    [InlineData("(-2,1.2)", "[[0,0],[0,1],[1,0],[1,1]]\n")]
    [InlineData("nan", "[[]]\n")]
    public async Task Jq_CombinationsCollectsTheCountFilterBeforeProducingOneProduct(string counts, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("[0,1]\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "[combinations(" + counts + ")]")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("0", "error(\"late\")", "late")]
    [InlineData("1", "error(\"late\")", "late")]
    [InlineData("0", "\"bad\"", "Range bounds must be numeric")]
    public async Task Jq_CombinationsLateCountErrorsPreventAllProducts(string first, string last, string message)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "[0,1]\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "combinations((" + first + "," + last + "))", "/input")));
        Assert.Equal(5, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error (at /input:1): " + message + "\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("8192")]
    [InlineData("65536")]
    [InlineData("2147483648")]
    [InlineData("infinite")]
    public async Task Jq_CombinationsBorrowsEmptyRepeatedRows(string count)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "[]|combinations(" + count + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
        Assert.InRange(root.PeakBytes, 1, 64 * 1024);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("1e100")]
    [InlineData("infinite")]
    [InlineData("(2147483647,1)")]
    public async Task Jq_CombinationsPreflightsUnrepresentableProductState(string count)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "try ([0]|combinations(" + count + ")) catch 42")));
        Assert.Equal(5, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: array cannot be represented\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_CombinationsRetainsItsInputWhileCountReadsConsumeLaterRecords()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"é\",{\"x\":1}]\n0\n1\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "[combinations((input,input))]")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[[\"é\"],[{\"x\":1}]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_CombinationsCountCancellationPreventsProductsAndClosesOwnedInput()
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "[0,1]\n");
        host.BeforeByteAppend = cancellation.Cancel;
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "combinations((0,(1|debug)))", "/input")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, root, cancellation.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_CombinationsProductCancellationPreservesEarlierOutputAndClosesInput()
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "[0,1]\n");
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 2) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "combinations((1,1))", "/input")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, root, cancellation.Token));
        Assert.Equal("[0,0]\n[0,1]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_CombinationsCountProductsKeepOuterStreamsBoundedAndStopOnClosure(bool close)
    {
        const int records = 2500;
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = !close };
        host.AddFile("/input", string.Concat(Enumerable.Repeat("[0]\n", records)));
        host.AddFile("/later", "unused\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "combinations((0,1))", "/input", "/later")));
        if (!close) host.AddFile("/later", "");
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("[0]\n", close ? 1 : records)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(close ? 1 : 2, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
