using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"useful but not for é\"", "ascii_upcase", "\"USEFUL BUT NOT FOR é\"\n")]
    [InlineData("\"ABCxyzÉ\"", "ascii_downcase", "\"abcxyzÉ\"\n")]
    [InlineData("\"  hello  \"", "trim", "\"hello\"\n")]
    [InlineData("\"\\u00a0hello\\u3000\"", "trim", "\"hello\"\n")]
    [InlineData("\"  hello  \"", "ltrim", "\"hello  \"\n")]
    [InlineData("\"  hello  \"", "rtrim", "\"  hello\"\n")]
    [InlineData("\"hello world\"", "ltrimstr(\"hello \")", "\"world\"\n")]
    [InlineData("\"hello world\"", "rtrimstr(\" world\")", "\"hello\"\n")]
    [InlineData("\"--hello--\"", "trimstr(\"--\")", "\"hello\"\n")]
    [InlineData("\"hello\"", "startswith(\"he\")", "true\n")]
    [InlineData("\"hello\"", "endswith(\"lo\")", "true\n")]
    public async Task Jq_CaseTrimAffix(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("123", "trim", "trim input must be a string")]
    [InlineData("1", "startswith(\"1\")", "startswith() requires string inputs")]
    [InlineData("\"a\"", "endswith(1)", "endswith() requires string inputs")]
    public async Task Jq_TrimAffixFailures(string input, string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
