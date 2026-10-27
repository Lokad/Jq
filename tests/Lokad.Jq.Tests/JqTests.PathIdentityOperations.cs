using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[1]", "path(.+null)", "[]\n")]
    [InlineData("[1]", "path(.+[])", "[]\n")]
    [InlineData("{\"a\":1}", "path(null+.)", "[]\n")]
    [InlineData("{\"a\":1}", "path(.*{})", "[]\n")]
    [InlineData("null", "(0+1)|path(.+0)", "[]\n")]
    [InlineData("null", "(0+1)|path((.+1)|.-1)", "[]\n")]
    [InlineData("true", "path(not|not)", "[]\n")]
    [InlineData("true", "path(toboolean)", "[]\n")]
    [InlineData("1", "path(tonumber)", "[]\n")]
    [InlineData("\"abc\"", "[path(tostring),path(@text),path(trim)]", "[[],[],[]]\n")]
    [InlineData("null", "def f:\"abc\"; f|path(f)", "[]\n")]
    [InlineData("[1]", "try path([]+.) catch .", "\"Invalid path expression with result [1]\"\n")]
    [InlineData("{\"a\":1}", "try path({}+.) catch .", "\"Invalid path expression with result {\\\"a\\\":1}\"\n")]
    [InlineData("null", "(0+1)|try path(.+1) catch .", "\"Invalid path expression with result 2\"\n")]
    public async Task Jq_PathIdentityFollowsBorrowedAndImmediateResults(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-c", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("abc", "ltrimstr(\"\")", "[{\"start\":0,\"end\":null}]\n")]
    [InlineData("abc", "rtrimstr(\"\")", "[{\"start\":null,\"end\":3}]\n")]
    [InlineData("abc", "trimstr(\"\")", "[{\"start\":0,\"end\":null},{\"start\":null,\"end\":3}]\n")]
    [InlineData("abc", "trimstr(\"x\")", "[]\n")]
    [InlineData("🚀abc", "ltrimstr(\"🚀\")", "[{\"start\":1,\"end\":null}]\n")]
    [InlineData("abcaa", "trimstr(\"a\")", "[{\"start\":1,\"end\":null},{\"start\":null,\"end\":3}]\n")]
    public async Task Jq_AffixPathsUseScalarSliceSegments(string text, string expression, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(System.Text.Json.JsonSerializer.Serialize(text));
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-c", "path(" + expression + ")"], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
