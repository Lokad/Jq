using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"Inventory\" | test(\"inventory\";\"i\")", "true\n")]
    [InlineData("\"Inventory\" | test(\"inventory\")", "false\n")]
    [InlineData("\"Inventory\" | test(\"vent\")", "true\n")]
    [InlineData("\"DÉSOLÉ 🚀\" | test(\"désolé .\";\"i\")", "true\n")]
    [InlineData("\"a\\nb\" | [test(\"^b\"),test(\"^b\";\"s\"),test(\"a.b\"),test(\"a.b\";\"m\"),test(\"^a.b$\";\"p\")]", "[false,false,false,false,true]\n")]
    [InlineData("\"AB\" | test(\"a b # comment\";\"ix\")", "true\n")]
    [InlineData("\"\" | [test(\"\"),test(\"\";\"n\")]", "[true,false]\n")]
    [InlineData("\"ba\" | test(\"a*\";\"ng\")", "true\n")]
    [InlineData("\"aaa\" | test(\"a\";\"g\")", "true\n")]
    [InlineData("\"Inventory\" | test(\"(?i)inventory\")", "true\n")]
    [InlineData("\"x\" | test(\"x\";null)", "true\n")]
    [InlineData("\"x\" | test([\"x\"])", "true\n")]
    [InlineData("\"Inventory\" | test([\"inventory\",\"i\"])", "true\n")]
    [InlineData("\"A\" | test((\"a\",\"A\");(\"\",\"i\"))", "false\ntrue\ntrue\ntrue\n")]
    [InlineData("\"A\" | test(empty;\"i\")", "")]
    [InlineData("\"A\" | test(\"A\";empty)", "")]
    [InlineData("null | test(empty)", "")]
    [InlineData("null | test(\"x\";empty)", "")]
    public async Task Jq_TestReturnsBooleansWithRegexFlagsAndArgumentStreams(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"x\" | test", "expects one or two")]
    [InlineData("\"x\" | test(\"x\";\"i\";\"g\")", "expects one or two")]
    public async Task Jq_TestArityMismatchesAreCompileErrors(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("null | test(\"x\")", "expected string")]
    [InlineData("\"x\" | test(1)", "expected string")]
    [InlineData("\"x\" | test(\"x\";1)", "expected string")]
    [InlineData("\"x\" | test([\"x\"];null)", "expected string")]
    [InlineData("\"x\" | test(\"[\")", "invalid regex")]
    [InlineData("\"x\" | test(\"x\";\"z\")", "unsupported test flag 'z'")]
    [InlineData("\"x\" | test(\"x\";\"l\")", "unsupported test flag 'l'")]
    [InlineData("\"x\" | test(\"x\" * 16385)", "regex pattern exceeds")]
    [InlineData("(\"a\" * 1000 + \"!\") | test(\"(*NO_START_OPT)(*NO_AUTO_POSSESS)(a+)+$\")", "regex matching failed")]
    [InlineData("(\"a\" * 6000) | test(\"(*NO_START_OPT)(*NO_AUTO_POSSESS)a.*b\")", "regex work limit exceeded")]
    public async Task Jq_TestReportsInvalidInputsAndRegexLimits(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
    [Fact]
    public async Task Jq_TestAndGsubSharePatternsAcrossInputRecords()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("\"Inventory\"\n", 2000)));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            """select(test("inventory";"i")) | gsub("inventory";"stock";"i")""")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("stock\n", 2000)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
