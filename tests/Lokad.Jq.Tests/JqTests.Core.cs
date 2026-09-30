using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("2 + 3 * 4", "14\n")]
    [InlineData("1+2", "3\n")]
    [InlineData("10 - 2 - 3", "5\n")]
    [InlineData("1 - -2", "3\n")]
    [InlineData(".5", "0.5\n")]
    [InlineData("1.", "1\n")]
    [InlineData("true | not", "false\n")]
    [InlineData("1 | not", "false\n")]
    [InlineData("null | not", "true\n")]
    [InlineData("1,2 | .+1", "2\n3\n")]
    [InlineData("3 | ., .+1", "3\n4\n")]
    public async Task Jq_CorePrecedenceAndLiterals(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ChainedComparisonsDoNotParse()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 < 2 < 3")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unexpected token", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }


    [Theory]
    [InlineData("[1,2,3,1] - [2]", "[\n  1,\n  3,\n  1\n]\n")]
    [InlineData("[1,2] - []", "[\n  1,\n  2\n]\n")]
    [InlineData("{\"a\":{\"x\":1,\"y\":2},\"b\":1} * {\"a\":{\"y\":3,\"z\":4}}", "{\n  \"a\": {\n    \"x\": 1,\n    \"y\": 3,\n    \"z\": 4\n  },\n  \"b\": 1\n}\n")]
    [InlineData("{\"a\":{\"x\":1}} + {\"a\":{\"y\":2}}", "{\n  \"a\": {\n    \"y\": 2\n  }\n}\n")]
    [InlineData("\"ab\" * 2.5", "\"abab\"\n")]
    [InlineData("2 * \"ab\"", "\"abab\"\n")]
    [InlineData("\"a,b\" / \",\"", "[\n  \"a\",\n  \"b\"\n]\n")]
    [InlineData("\"abc\" / \"\"", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"\" | split(\",\")", "[]\n")]
    [InlineData("\"abc\" | split(\"\")", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    public async Task Jq_OperatorTypeCombinations(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"6\" + 1", "cannot be added")]
    [InlineData("\"a\" - \"b\"", "cannot be subtracted")]
    [InlineData("\"a\" * {}", "cannot be multiplied")]
    [InlineData("\"a\" / 2", "cannot be divided")]
    [InlineData("\"a\" % \"b\"", "cannot be divided (remainder)")]
    public async Task Jq_MixedTypeArithmeticFails(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
    [Theory]
    [InlineData("empty // 42", "42\n")]
    [InlineData("(false, null, 1) // 42", "1\n")]
    [InlineData("(false, null, 1) | . // 42", "42\n42\n1\n")]
    [InlineData("1 // 2 // 3", "1\n")]
    public async Task Jq_AlternativeFiltersGoods(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AlternativeFallsBackAfterOnlyFalsyOutputs()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "(false, null) // (7, 8)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("7\n8\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1 | .foo?", "")]
    [InlineData("1 | .[0]?", "")]
    [InlineData("1 | .[]?", "")]
    [InlineData("1 | .[0:1]?", "")]
    [InlineData("(1 | .foo)?", "")]
    [InlineData("{\"a\":1} | .b?", "null\n")]
    [InlineData("null | .a?", "null\n")]
    [InlineData("(\"x\" | test(\"x\";\"z\"))?", "")]
    public async Task Jq_OptionalSuppressesCatchableErrors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_OptionalDoesNotSuppressQuota()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{a:range(0;300000)}? | length")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_RecursiveDescentMatchesManualExample()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[[{\"a\":1}]]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".. | .a?")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_RecursiveDescentVisitsPreOrder()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[[1]]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[..] | length")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("3\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("{\"k\":\"K\"} | {(.k): 1}", "{\n  \"K\": 1\n}\n")]
    [InlineData("{\"ks\":[\"a\",\"b\"]} | {(.ks[]): 1}", "{\n  \"a\": 1\n}\n{\n  \"b\": 1\n}\n")]
    [InlineData("{\"ks\":[\"a\",\"b\"],\"vs\":[1,2]} | {((.ks[])): (.vs[])}", "{\n  \"a\": 1\n}\n{\n  \"a\": 2\n}\n{\n  \"b\": 1\n}\n{\n  \"b\": 2\n}\n")]
    [InlineData("{\"foo\":1} | .\"foo\"", "1\n")]
    [InlineData("{\"a\":{\"b\":2}} | .a.\"b\"", "2\n")]
    [InlineData("{\"a\":1} | {\"a$\\(1+1)\": 2}", "{\n  \"a$2\": 2\n}\n")]
    public async Task Jq_DynamicAndQuotedKeys(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    // Constant non-string keys fail at compile time, matching key validation.
    [InlineData("{(0):1}", "jq: Cannot use number (0) as object key at line 1 column 2 (filter)\n")]
    [InlineData("{(true):1}", "jq: Cannot use boolean (true) as object key at line 1 column 2 (filter)\n")]
    public async Task Jq_ConstantNonStringKeysFailAtCompile(string filter, string expectedError)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expectedError, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("# pick the field\n.foo", "1\n")]
    [InlineData("{\n\"a\": 1\n}", "{\n  \"a\": 1\n}\n")]
    public async Task Jq_CommentsAndLayouts(string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"foo\":1}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"a\\qb\"", "invalid escape")]
    [InlineData("`", "invalid character")]
    public async Task Jq_LexerErrorsIdentifyTheirSpan(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Contains("line 1 column", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_BareInterpolatedKeysReadInputFields()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":1,\"b\":2,\"a$2\":4}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "{\"a\",b,\"a$\\(1+1)\"}")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 2,\n  \"a$2\": 4\n}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NestedQuotedInterpolation()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"(\":1}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "\"a\\(.[\"(\"])b\"")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"a1b\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
