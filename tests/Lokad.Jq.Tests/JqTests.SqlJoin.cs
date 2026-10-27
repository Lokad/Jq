using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[1,2,1]", "[1]", "[0,2]")]
    [InlineData("[1,2,1]", "[]", "[]")]
    [InlineData("[10,20,30]", "{start:0,end:2}", "[10,20]")]
    [InlineData("[10,20,30]", "{start:1.5,end:null}", "[20,30]")]
    [InlineData("[10,20,30]", "1.5", "20")]
    [InlineData("[10,20,30]", "-1", "30")]
    [InlineData("[10,20,30]", "nan", "null")]
    [InlineData("{a:1}", "\"a\"", "1")]
    [InlineData("null", "\"a\"", "null")]
    [InlineData("null", "0", "null")]
    [InlineData("null", "{}", "null")]
    public async Task Jq_SqlJoinUsesOrdinaryIndexReads(string index, string key, string value)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"x\",\"y\"]");
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "JOIN(" + index + "; .[]; " + key + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("[\"x\"," + value + "]\n[\"y\"," + value + "]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("[]")]
    public async Task Jq_SqlJoinRejectsInvalidNullKeysAfterEarlierOutput(string key)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"x\"]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "\"prefix\", JOIN(null; .[]; " + key + ")")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("\"prefix\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error (at <stdin>:1): cannot index null\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("empty", "[\"x\"]\n[\"y\"]\n", "")]
    [InlineData("(\"a\",\"b\") | debug", "[\"x\",1,2]\n[\"y\",1,2]\n",
        "[\"DEBUG:\",\"a\"]\n[\"DEBUG:\",\"b\"]\n[\"DEBUG:\",\"a\"]\n[\"DEBUG:\",\"b\"]\n")]
    public async Task Jq_SqlJoinRetainsKeyMultiplicityAndEffects(string key, string expected, string error)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"x\",\"y\"]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "JOIN({a:1,b:2}; .[]; " + key + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_SqlJoinStopsDeferredRowsWhenOutputCloses()
    {
        var host = new MockFileSystem { AppendRemainsOpen = false };
        host.AddFile("/first", "[\"x\",\"y\"]");
        host.AddFile("/later", "0");
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "JOIN([1,2,1]; (.[],error(\"later\")); [1])", "/first", "/later")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("[\"x\",[0,2]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
