using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[0]", "path(paths)", "Invalid path expression with result [0]")]
    [InlineData("[0]", "path(paths(true))", "Invalid path expression with result [0]")]
    [InlineData("[0]", "paths |= 7", "Invalid path expression with result [0]")]
    [InlineData("{\"1\":1}", "path(INDEX(tostring))", "Invalid path expression with result {\"1\":1}")]
    [InlineData("{\"1\":1}", "INDEX(tostring) |= 7", "Invalid path expression with result {\"1\":1}")]
    [InlineData("{}", "path(INDEX(tostring))", "Invalid path expression with result {}")]
    [InlineData("[1,2]", "path(JOIN({a:2};.[0];\"a\"))", "Invalid path expression near attempt to access element \"a\" of {\"a\":2}")]
    [InlineData("[1,2]", "JOIN({a:2};.[0];\"a\") |= 7", "Invalid path expression near attempt to access element \"a\" of {\"a\":2}")]
    [InlineData("[1,2]", "path(JOIN(null;.;\"x\";.[0]))", "Invalid path expression near attempt to access element \"x\" of null")]
    [InlineData("[1,2]", "path(JOIN(.;.;0;.[0]))", "Invalid path expression near attempt to access element 0 of [[1,2],1]")]
    public async Task Jq_FreshResultsDoNotBecomeWritablePaths(string input, string source, string diagnostic)
    {
        var host = new MockFileSystem();
        host.AddFile("/input", input);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "\"prefix\", (" + source + ")", "/input")));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("\"prefix\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error (at /input:1): " + diagnostic + "\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("[1,2]", ". as $saved | path([.] | $saved)", "[]\n")]
    [InlineData("null", ". as $saved | path(JOIN(null;.;empty;$saved))", "[]\n")]
    [InlineData("{\"a\":2}", ". as $saved | path(JOIN(null;.;empty;$saved))", "[]\n")]
    [InlineData("[1,2]", ". as $saved | path(JOIN(.;.;(0,1);$saved))", "[]\n")]
    [InlineData("[1,2]", ". as $saved | path(JOIN(null;.;empty;$saved|.[0]))", "[0]\n")]
    [InlineData("{\"a\":[0]}", ".a as $saved | path(.a | [.] | $saved)", "[\"a\"]\n")]
    [InlineData("[0]", "path(paths(false))", "")]
    [InlineData("[0]", "path(JOIN(null;.;empty;empty))", "")]
    public async Task Jq_PathTrackingRecoversBorrowedBindingsAndKeepsEmptyStreams(string input, string source, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_JoinPathErrorsStopLaterKeyAndJoinEffects()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1,2]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "try path(JOIN({a:2};.;((\"a\"|debug),(\"b\"|debug));debug)) catch \"caught\"")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"caught\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",\"a\"]\n", host.GetOutput(JqFileDescriptor.StdErr));
    }
}
