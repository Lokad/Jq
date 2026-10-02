using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"a,b, c\" | [scan(\", \")]", "[\n  \", \"\n]\n")]
    [InlineData("\"a,b, c, d, e,f\" | [scan(\", *\")]", "[\n  \",\",\n  \", \",\n  \", \",\n  \", \",\n  \",\"\n]\n")]
    [InlineData("\"abcABBBCabbbc\" | [scan(\"b+\"; \"i\")]", "[\n  \"b\",\n  \"BBB\",\n  \"bbb\"\n]\n")]
    [InlineData("\"aba\" | [scan(\"a*\")]", "[\n  \"a\",\n  \"\",\n  \"a\",\n  \"\"\n]\n")]
    [InlineData("\"\" | [scan(\"b+\")]", "[]\n")]
    [InlineData("\"abAB\" | [scan(\"a\"; \"gi\")]", "[\n  \"a\",\n  \"A\"\n]\n")]
    [InlineData("\"a1 b2\" | [scan(\"(?<c>[a-z])(?<n>[0-9])\")]", "[\n  [\n    \"a\",\n    \"1\"\n  ],\n  [\n    \"b\",\n    \"2\"\n  ]\n]\n")]
    [InlineData("\"1\" | [scan(\"(?<x>y)?([0-9])\")]", "[\n  [\n    null,\n    \"1\"\n  ]\n]\n")]
    [InlineData("[\"\",\"bBb\",\"abcABBBCabbbc\"] | [.[] | scan(\"b+\"; \"i\")]", "[\n  \"bBb\",\n  \"b\",\n  \"BBB\",\n  \"bbb\"\n]\n")]
    [InlineData("\"abcdefabc\" | [scan(\"c\")]", "[\n  \"c\",\n  \"c\"\n]\n")]
    [InlineData("\"abaabbaaabbb\" | [scan(\"(a+)(b+)\")]", "[\n  [\n    \"a\",\n    \"b\"\n  ],\n  [\n    \"aa\",\n    \"bb\"\n  ],\n  [\n    \"aaa\",\n    \"bbb\"\n  ]\n]\n")]
    public async Task Jq_ScanVectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"ab\" | [splits(\"\")]", "[\n  \"\",\n  \"a\",\n  \"b\",\n  \"\"\n]\n")]
    [InlineData("\"ab\" | [splits(\"c\")]", "[\n  \"ab\"\n]\n")]
    [InlineData("\"abAABBabA\" | [splits(\"a+\"; \"i\")]", "[\n  \"\",\n  \"b\",\n  \"BB\",\n  \"b\",\n  \"\"\n]\n")]
    [InlineData("\"abAABBabA\" | [splits(\"b+\"; \"i\")]", "[\n  \"a\",\n  \"AA\",\n  \"a\",\n  \"A\"\n]\n")]
    [InlineData("\"a,é🚀,b\" | [splits(\",\")]", "[\n  \"a\",\n  \"é\\uD83D\\uDE80\",\n  \"b\"\n]\n")]
    [InlineData("\"a,b, c\" | split(\", *\"; \"\")", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"aXbXc\" | split(\"x\"; \"i\")", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"a,b,c\" | split(\",\")", "[\n  \"a\",\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"ab,cd, ef\" | split(\", *\"; null)", "[\n  \"ab\",\n  \"cd\",\n  \"ef\"\n]\n")]
    [InlineData("\"ab,cd,   ef, gh\" | [splits(\", *\")]", "[\n  \"ab\",\n  \"cd\",\n  \"ef\",\n  \"gh\"\n]\n")]
    [InlineData("\"ab,cd ef,  gh\" | [splits(\",? *\"; \"n\")]", "[\n  \"ab\",\n  \"cd\",\n  \"ef\",\n  \"gh\"\n]\n")]
    // Empty patterns split at every gap, including both ends of the input.
    [InlineData("\"\" | [splits(\"\")]", "[\n  \"\",\n  \"\"\n]\n")]
    [InlineData("\"abc\" | [splits(\"\")]", "[\n  \"\",\n  \"a\",\n  \"b\",\n  \"c\",\n  \"\"\n]\n")]
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
    [InlineData("\"a\" | [sub(\"a\"; \"b\", \"c\")]", "[\n  \"b\",\n  \"c\"\n]\n")]
    [InlineData("\"aB\" | [sub(\"(?<a>.)\"; \"\\(.a|ascii_upcase)\", \"\\(.a|ascii_downcase)\", \"c\")]", "[\n  \"AB\",\n  \"aB\",\n  \"cB\"\n]\n")]
    [InlineData("\"aaa\" | sub(\"a\"; \"X\")", "\"Xaa\"\n")]
    [InlineData("\"aaa\" | sub(\"a\"; \"X\"; \"g\")", "\"XXX\"\n")]
    [InlineData("\"123abc456def\" | sub(\"[^a-z]*(?<x>[a-z]+)\"; \"Z\\(.x)\"; \"g\")", "\"ZabcZdef\"\n")]
    [InlineData("\"abc\" | sub(\"z\"; \"X\")", "\"abc\"\n")]
    [InlineData("\"ab\" | sub(\"\"; \"X\")", "\"Xab\"\n")]
    [InlineData("\"a1\" | [scan(\"(?<c>[a-z])(?<n>[0-9])\")]", "[\n  [\n    \"a\",\n    \"1\"\n  ]\n]\n")]
    [InlineData("\"aB\" | [sub(\"(?<a>.)\"; \"\\(.a|ascii_upcase)\", \"\\(.a|ascii_downcase)\")]", "[\n  \"AB\",\n  \"aB\"\n]\n")]
[InlineData("[\"a,b, c, d, e,f\", \", a,b, c, d, e,f, \"] | [.[] | sub(\", \"; \":\")]", "[\n  \"a,b:c, d, e,f\",\n  \":a,b, c, d, e,f, \"\n]\n")]
[InlineData("[\"a,b, c, d, e,f\", \", a,b, c, d, e,f, \"] | [.[] | scan(\", \")]", "[\n  \", \",\n  \", \",\n  \", \",\n  \", \",\n  \", \",\n  \", \",\n  \", \",\n  \", \"\n]\n")]
    [InlineData("[\"a,b, c, d, e,f\", \", a,b, c, d, e,f, \"] | [.[]|[[sub(\", *\";\":\")], [gsub(\", *\";\":\")], [scan(\", *\")]]]", "[\n  [\n    [\n      \"a:b, c, d, e,f\"\n    ],\n    [\n      \"a:b:c:d:e:f\"\n    ],\n    [\n      \",\",\n      \", \",\n      \", \",\n      \", \",\n      \",\"\n    ]\n  ],\n  [\n    [\n      \":a,b, c, d, e,f, \"\n    ],\n    [\n      \":a:b:c:d:e:f:\"\n    ],\n    [\n      \", \",\n      \",\",\n      \", \",\n      \", \",\n      \", \",\n      \",\",\n      \", \"\n    ]\n  ]\n]\n")]
    [InlineData("[\"a,b, c, d, e,f\", \", a,b, c, d, e,f, \"] | [.[]|[[sub(\", +\";\":\")], [gsub(\", +\";\":\")], [scan(\", +\")]]]", "[\n  [\n    [\n      \"a,b:c, d, e,f\"\n    ],\n    [\n      \"a,b:c:d:e,f\"\n    ],\n    [\n      \", \",\n      \", \",\n      \", \"\n    ]\n  ],\n  [\n    [\n      \":a,b, c, d, e,f, \"\n    ],\n    [\n      \":a,b:c:d:e,f:\"\n    ],\n    [\n      \", \",\n      \", \",\n      \", \",\n      \", \",\n      \", \"\n    ]\n  ]\n]\n")]
    [InlineData("\"\u2019\" | sub(\"(?<x>.)\"; \"\\(.x)!\")", "\"\u2019!\"\n")]
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
    [InlineData("\"x\" | scan(null)", "null (null) is not a string")]
    [InlineData("\"x\" | sub(null; \"y\")", "null (null) is not a string")]
    [InlineData("\"x\" | splits(null)", "null (null) is not a string")]
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
