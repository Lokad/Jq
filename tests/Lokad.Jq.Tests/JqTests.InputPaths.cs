using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_InputPathsReleaseResolutionWorkBetweenSources()
    {
        string directory = "/" + new string('x', 2048);
        string[] files = Enumerable.Range(0, 1000).Select(i => "file" + i).ToArray();
        var host = new MockFileSystem();
        foreach (string file in files) host.AddFile(directory + "/" + file, "42\n");
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-c", ".", .. files], [new("PWD", directory)]);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("42\n", files.Length)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(files.Length, host.ReadBytesCallCount);
        Assert.Equal(files.Length, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InputPathsReleaseOwnershipOnCancelledRead()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem { BeforeByteRead = _ => cancellation.Cancel() };
        host.AddFile("/work/data", "42");
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-c", ".", "data"], [new("PWD", "/work")]);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("null", "")]
    [InlineData("null", "/../../unused")]
    [InlineData("first", "")]
    [InlineData("first", "/../../unused")]
    [InlineData("halt", "")]
    [InlineData("halt", "/../../unused")]
    [InlineData("closed", "")]
    [InlineData("closed", "/../../unused")]
    public async Task Jq_InputPathsWaitForDemand(string mode, string unused)
    {
        var host = new MockFileSystem { AppendRemainsOpen = mode != "closed" };
        host.AddFile("/first", "42\n43\n");
        string[] arguments = mode switch
        {
            "null" => ["-nc", "42", unused],
            "first" => ["-nc", "first(inputs)", "/first", unused],
            "halt" => ["-c", "halt", "/first", unused],
            _ => ["-c", ".", "/first", unused],
        };
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(mode == "halt" ? "" : "42\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(mode == "null" ? 0 : 1, host.ReadBytesCallCount);
        Assert.Equal(mode == "null" ? 0 : 1, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public async Task Jq_InputPathsDoNotExpandUnusedDirectoryPrefixes(int operands)
    {
        string directory = "/" + new string('x', 4096);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nc", "42", .. Enumerable.Repeat("unused", operands)], [new("PWD", directory)]);
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("42\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.True(memory.PeakBytes < 8192 + 80 + directory.Length);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("", "jq: cannot open : path not found\n")]
    [InlineData("/../data", "jq: cannot open /../data: path not found\n")]
    public async Task Jq_InputPathsDelegateDemandedLookupFailure(string operand, string expected)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".", operand)));
        Assert.Equal(2, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
