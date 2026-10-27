using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("def café: 42; café")]
    [InlineData("1\u00a0+2")]
    [InlineData("1\u2003+2")]
    [InlineData("1\v+2")]
    [InlineData("1\f+2")]
    [InlineData(".café")]
    [InlineData("1١")]
    [InlineData("\"\\u  41\"")]
    [InlineData("\"\\u+041\"")]
    [InlineData("\"\\u12\"")]
    [InlineData("\"\\u12xz\"")]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uD800x\"")]
    [InlineData("\"\\uD800\\u0041\"")]
    [InlineData("\"\\uD800\\\\uDC00\"")]
    public async Task Jq_SourceAlphabetAndEscapesRejectInvalidSyntax(string source)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", source], []);
        Assert.Equal(3, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.StartsWith("jq: ", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("1\t+2", "3\n")]
    [InlineData("1\r\n+2", "3\n")]
    [InlineData("{\"café\":1}|.\"café\"", "1\n")]
    [InlineData("\"caf\\u00E9\"", "\"café\"\n")]
    [InlineData("\"\\uD83D\\uDE00\"|explode", "[128512]\n")]
    [InlineData("\"\\uDC00\"|explode", "[65533]\n")]
    [InlineData("\"\\u005c(1)\"", "\"\\\\(1)\"\n")]
    [InlineData("\"\\u005c\\(1)\"", "\"\\\\1\"\n")]
    [InlineData("\"\\\\\\u0028\"", "\"\\\\(\"\n")]
    [InlineData("\"x\\(\"\\u005c(1)\")\"", "\"x\\\\(1)\"\n")]
    public async Task Jq_SourceUnicodeDataAndEscapedBackslashesKeepTheirMeaning(string source, string expected)
    {
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", source], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
