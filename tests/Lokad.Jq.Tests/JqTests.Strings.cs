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

    [Theory]
    [InlineData("1", "tonumber", "1\n")]
    [InlineData("\"1.5\"", "tonumber", "1.5\n")]
    [InlineData("\"42\"", "tonumber | type", "\"number\"\n")]
    [InlineData("\"+5.43\"", "tonumber", "5.43\n")]
    [InlineData("\" 4\"", "try tonumber catch \"caught\"", "\"caught\"\n")]
    [InlineData("\"True\"", "try toboolean catch \"caught\"", "\"caught\"\n")]
    [InlineData("[\"false\", \"true\", false, true]", "map(toboolean)", "[\n  false,\n  true,\n  false,\n  true\n]\n")]
    [InlineData("[1, \"a\", true, null]", "map(tostring)", "[\n  \"1\",\n  \"a\",\n  \"true\",\n  \"null\"\n]\n")]
    [InlineData("\"hello\"", "utf8bytelength", "5\n")]
    [InlineData("\"é🚀\"", "utf8bytelength", "6\n")]
    public async Task Jq_Conversions(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("null", "tonumber", "null (null) cannot be parsed as a number")]
    [InlineData("5", "utf8bytelength", "only strings have UTF-8 byte length")]
    [InlineData("null", "toboolean", "null (null) cannot be parsed as a boolean")]
    [InlineData("0", "toboolean", "number (0) cannot be parsed as a boolean")]
    public async Task Jq_ConversionFailures(string input, string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("[\"1\",2,true,false,3.4]", "join(\",\")", "\"1,2,true,false,3.4\"\n")]
    [InlineData("[[],[null],[null,null],[null,null,null]]", ".[] | join(\",\")", "\"\"\n\"\"\n\",\"\n\",,\"\n")]
    [InlineData("[[\"a\",null],[null,\"a\"]]", ".[] | join(\",\")", "\"a,\"\n\",a\"\n")]
    [InlineData("[[],[\"\"],[\"\",\"\"],[\"\",\"\",\"\"]]", "[.[]|join(\"a\")]", "[\n  \"\",\n  \"\",\n  \"a\",\n  \"aa\"\n]\n")]
    [InlineData("null", "join(\",\")", "\"\"\n")]
    [InlineData("[]", "join(\",\")", "\"\"\n")]
    [InlineData("\"a,b,c\"", "split(\",\")", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"a,\"", "split(\",\")", "[\n  \"a\",\n  \"\"\n]\n")]
    [InlineData("\"\"", "split(\",\")", "[]\n")]
    [InlineData("\"a,,b\"", "split(\",\")", "[\n  \"a\",\n  \"\",\n  \"b\"\n]\n")]
    [InlineData("\"abc\"", "split(\"\")", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"hello\"", "explode", "[\n  104,\n  101,\n  108,\n  108,\n  111\n]\n")]
    [InlineData("\"a\\u0000b\"", "explode", "[\n  97,\n  0,\n  98\n]\n")]
    [InlineData("[104,101]", "implode", "\"he\"\n")]
    [InlineData("[-1,1114112,55296,1.9]", "implode|explode", "[\n  65533,\n  65533,\n  65533,\n  1\n]\n")]
    public async Task Jq_SplitJoinExplode(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1", "split(\",\")", "split input and separator must be strings")]
    [InlineData("\"a\"", "split(1)", "split input and separator must be strings")]
    [InlineData("5", "join(\",\")", "cannot iterate over number")]
    [InlineData("[\"1\",\"2\",{\"a\":{\"b\":{\"c\":33}}}]", "join(\",\")", "string (\"1,2,\") and object ({\"a\":{\"b\":{\"c\":33}}}) cannot be added")]
    [InlineData("[\"1\",\"2\",[3,4,5]]", "join(\",\")", "string (\"1,2,\") and array ([3,4,5]) cannot be added")]
    [InlineData("5", "explode", "explode input must be a string")]
    [InlineData("123", "implode", "implode input must be an array")]
    [InlineData("[\"a\"]", "implode", "string (\"a\") can't be imploded, unicode codepoint needs to be numeric")]
    public async Task Jq_SplitJoinExplodeFailures(string input, string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("\"\\\\(\"", "\"\\\\(\"\n")]
    [InlineData("\"a\\\\(.)\"", "\"a\\\\(.)\"\n")]
    [InlineData("\"\\\\\\\\\"", "\"\\\\\\\\\"\n")]
    [InlineData("\"\\\\(1+2)\"", "\"\\\\(1+2)\"\n")]
    [InlineData("\"\\\\\\(1+2)\"", "\"\\\\3\"\n")]
    public async Task Jq_LiteralBackslashParen(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_UnterminatedInterpolationIsCompileError()
    {
        // The lexer depth-tracking rejects the unclosed marker before evaluation,
        // so no try/catch handler can observe it.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try \"\\(\" catch .")));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unterminated string", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
