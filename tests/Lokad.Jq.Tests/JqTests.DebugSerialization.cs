using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("debug")]
    [InlineData("debug(.)")]
    public async Task Jq_DebugTreeLoggingUsesBoundedStorage(string effect)
    {
        string input = "[" + string.Join(',', Enumerable.Range(0, 128)) + "]";
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ". as $x | range(256) | $x | " + effect + " | empty")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("[\"DEBUG:\"," + input + "]\n", 256))),
            host.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(256, host.AppendCallCount);
        Assert.Equal(0, memory.Counts.Refusals);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_DebugBorrowedEnvelopePreservesFormattingAndValueIdentity()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"z\":1,\"a\":\"é\"}");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-acS", ". as $x | debug | path($x)")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("[\"DEBUG:\",{\"a\":\"\\u00e9\",\"z\":1}]\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal("[]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(63, 0)]
    [InlineData(64, 5)]
    public async Task Jq_DebugEnvelopeRetainsConstructionDepthProtection(int depth, int status)
    {
        string input = new string('[', depth) + "0" + new string(']', depth);
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "try debug catch \"caught\" | empty")));
        Assert.Equal(status, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(status == 0 ? "[\"DEBUG:\"," + input + "]\n" : "jq: value nesting limit exceeded\n",
            host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
