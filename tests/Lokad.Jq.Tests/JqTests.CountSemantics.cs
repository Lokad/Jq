using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("limit")]
    [InlineData("skip")]
    [InlineData("nth")]
    public async Task Jq_StringCountsFailAfterTheFirstGeneratorEffect(string operation)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        string filter = "try [" + operation + "(\"2\"; (debug, (42|debug)))] catch .";
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("\"string (\\\"2\\\") and number (1) cannot be subtracted\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",null]\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("skip")]
    [InlineData("nth")]
    public async Task Jq_NonnumericCountsKeepEmptyGeneratorEffects(string operation)
    {
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nc", "try " + operation + "([]; (debug|empty)) catch ."], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",null]\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[skip(1e100;(1,2,3)),nth(1e100;(1,2,3))]", "[]\n")]
    [InlineData("[limit(1.5;(1,2,3)),skip(1.5;(1,2,3)),nth(1.5;(1,2,3))]", "[1,2,2,3,2]\n")]
    [InlineData("try [range(\"2\")] catch .", "\"Range bounds must be numeric\"\n")]
    [InlineData("try [range(\"0\";2)] catch .", "\"Range bounds must be numeric\"\n")]
    [InlineData("[limit(3;range(\"0\";\"2\";\"1\"))]", "[\"0\",\"01\",\"011\"]\n")]
    [InlineData("try [range(3;0;false)] catch .", "\"number (3) and boolean (false) cannot be added\"\n")]
    [InlineData("[range(0;0;\"x\"),range(0;1;false)]", "[]\n")]
    [InlineData("[range(3;0;nan)]", "[3]\n")]
    public async Task Jq_CountAndRangeOperationsFollowTheirReferenceBranches(string filter, string expected)
    {
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ThreeArgumentRangeBorrowsItsInitialValueUntilUpdated()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1]\n");
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-c", "[first(path(range(.;{};[]))),try path(limit(2;range(.;{};[1]))) catch .]"], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[[],[],\"Invalid path expression with result [1,1]\"]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
