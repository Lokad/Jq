using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"Inventory\" | test(\"inventory\";\"i\")", "true\n")]
    [InlineData("\"Inventory\" | test(\"inventory\")", "false\n")]
    [InlineData("\"Inventory\" | test(\"vent\")", "true\n")]
    [InlineData("\"DÉSOLÉ 🚀\" | test(\"désolé .\";\"i\")", "true\n")]
    [InlineData("\"a\\nb\" | [test(\"^b\"),test(\"^b\";\"s\"),test(\"a.b\"),test(\"a.b\";\"m\"),test(\"^a.b$\";\"p\")]", "[\n  false,\n  false,\n  false,\n  false,\n  true\n]\n")]
    [InlineData("\"AB\" | test(\"a b # comment\";\"ix\")", "true\n")]
    [InlineData("\"\" | [test(\"\"),test(\"\";\"n\")]", "[\n  true,\n  false\n]\n")]
    [InlineData("\"ba\" | test(\"a*\";\"ng\")", "true\n")]
    [InlineData("\"aaa\" | test(\"a\";\"g\")", "true\n")]
    [InlineData("\"Inventory\" | test(\"(?i)inventory\")", "true\n")]
    [InlineData("\"x\" | test(\"x\";null)", "true\n")]
    [InlineData("\"x\" | test([\"x\"])", "true\n")]
    [InlineData("\"Inventory\" | test([\"inventory\",\"i\"])", "true\n")]
    [InlineData("\"A\" | test((\"a\",\"A\");(\"\",\"i\"))", "false\ntrue\ntrue\ntrue\n")]
    [InlineData("\"ab\" | test((\"a\",\"b\"))", "true\ntrue\n")]
    [InlineData("\"A\" | test(empty;\"i\")", "")]
    [InlineData("\"A\" | test(\"A\";empty)", "")]
    [InlineData("null | test(empty)", "")]
    [InlineData("null | test(\"x\";empty)", "")]
    [InlineData("\"abc\" | [test(\"( )*\"; \"gn\")]", "[\n  false\n]\n")]
    [InlineData("\"\\u0101\" | [test(\"\\u0101\")]", "[\n  true\n]\n")]
    [InlineData("\"foo\" | test(\"foo\")", "true\n")]
    [InlineData("[\"xabcd\", \"ABC\"] | .[] | test(\"a b c # spaces are ignored\"; \"ix\")", "true\ntrue\n")]
    [InlineData("\"ab\" | test(\"(?<x>a)(?<x>b)\")", "true\n")]
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
    [InlineData("null | test(\"x\")", "null (null) cannot be matched, as it is not a string")]
    [InlineData("\"x\" | test(1)", "number not a string or array")]
    [InlineData("\"x\" | test(null)", "null not a string or array")]
    [InlineData("\"x\" | test(\"x\";1)", "number (1) is not a string")]
    [InlineData("\"x\" | test([\"x\"];null)", "array ([\"x\"]) is not a string")]
    [InlineData("\"x\" | test(\"[\")", "invalid regex")]
    [InlineData("\"x\" | test(\"x\";\"z\")", "z is not a valid modifier string")]
    [InlineData("\"x\" | test(\"x\";\"l\")", "unsupported regex flag")]
    [InlineData("\"x\" | test(\"x\" * 16385)", "regex pattern exceeds")]
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
