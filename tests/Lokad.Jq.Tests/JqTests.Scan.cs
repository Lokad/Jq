using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"a,b, c\" | [scan(\", \")]", "[\", \"]\n")]
    [InlineData("\"a,b, c, d, e,f\" | [scan(\", *\")]", "[\",\",\", \",\", \",\", \",\",\"]\n")]
    [InlineData("\"abcABBBCabbbc\" | [scan(\"b+\"; \"i\")]", "[\"b\",\"BBB\",\"bbb\"]\n")]
    [InlineData("\"\" | [scan(\"b+\")]", "[]\n")]
    [InlineData("\"abAB\" | [scan(\"a\"; \"gi\")]", "[\"a\",\"A\"]\n")]
    [InlineData("\"a1 b2\" | [scan(\"(?<c>[a-z])(?<n>[0-9])\")]", "[[\"a\",\"1\"],[\"b\",\"2\"]]\n")]
    [InlineData("\"1\" | [scan(\"(?<x>y)?([0-9])\")]", "[[null,\"1\"]]\n")]
    public async Task Jq_ScanVectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"ab\" | [splits(\"\")]", "[\"\",\"a\",\"b\",\"\"]\n")]
    [InlineData("\"ab\" | [splits(\"c\")]", "[\"ab\"]\n")]
    [InlineData("\"abAABBabA\" | [splits(\"a+\"; \"i\")]", "[\"\",\"b\",\"BB\",\"b\",\"\"]\n")]
    [InlineData("\"abAABBabA\" | [splits(\"b+\"; \"i\")]", "[\"a\",\"AA\",\"a\",\"A\"]\n")]
    [InlineData("\"a,é🚀,b\" | [splits(\",\")]", "[\"a\",\"é\\uD83D\\uDE80\",\"b\"]\n")]
    [InlineData("\"a,b, c\" | split(\", *\"; \"\")", "[\"a\",\"b\",\"c\"]\n")]
    [InlineData("\"aXbXc\" | split(\"x\"; \"i\")", "[\"a\",\"b\",\"c\"]\n")]
    [InlineData("\"a,b,c\" | split(\",\")", "[\"a\",\"b\",\"c\"]\n")]
    public async Task Jq_SplitsVectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"a,b, c, d, e,f\" | sub(\", \"; \":\")", "\"a,b:c, d, e,f\"\n")]
    [InlineData("\", a,b, c, d, e,f, \" | sub(\", \"; \":\")", "\":a,b, c, d, e,f, \"\n")]
    [InlineData("\"abcdef\" | sub(\"^(?<head>.)\"; \"Head=\\(.head) Tail=\")", "\"Head=a Tail=bcdef\"\n")]
    [InlineData("\"a\" | [sub(\"a\"; \"b\", \"c\")]", "[\"b\",\"c\"]\n")]
    [InlineData("\"aB\" | [sub(\"(?<a>.)\"; \"\\(.a|ascii_upcase)\", \"\\(.a|ascii_downcase)\", \"c\")]", "[\"AB\",\"aB\",\"cB\"]\n")]
    [InlineData("\"aaa\" | sub(\"a\"; \"X\")", "\"Xaa\"\n")]
    [InlineData("\"aaa\" | sub(\"a\"; \"X\"; \"g\")", "\"XXX\"\n")]
    [InlineData("\"abc\" | sub(\"z\"; \"X\")", "\"abc\"\n")]
    [InlineData("\"ab\" | sub(\"\"; \"X\")", "\"Xab\"\n")]
    [InlineData("\"a1\" | [scan(\"(?<c>[a-z])(?<n>[0-9])\")]", "[[\"a\",\"1\"]]\n")]
    public async Task Jq_SubVectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"x\" | scan([\"x\"])", "array ([\"x\"]) is not a string")]
    [InlineData("\"ab\" | splits(\"a\"; 5)", "number (5) is not a string")]
    [InlineData("\"ab\" | split(\"a\"; 5)", "number (5) is not a string")]
    [InlineData("5 | scan(\"x\")", "number (5) cannot be matched, as it is not a string")]
    [InlineData("5 | splits(\"x\")", "number (5) cannot be matched, as it is not a string")]
    [InlineData("5 | sub(\"x\"; \"y\")", "number (5) cannot be matched, as it is not a string")]
    [InlineData("\"x\" | sub(\"x\"; \"y\"; \"z\")", "z is not a valid modifier string")]
    public async Task Jq_RegexStreamFailures(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("\"x\" | scan(\"a\";\"b\";\"c\")", "expects one or two")]
    [InlineData("\"x\" | splits(\"a\";\"b\";\"c\")", "expects one or two")]
    [InlineData("\"x\" | split(\"a\";\"b\";\"c\")", "expects one or two")]
    [InlineData("\"x\" | sub(\"a\")", "expects two or three")]
    public async Task Jq_RegexStreamArityIsCompileError(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
