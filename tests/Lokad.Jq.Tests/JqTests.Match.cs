using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    // Oracle vectors: external/jq tests/onig.test, compared by value upstream.
    // Capture key order below is uniform (offset, length, string, name).
    [InlineData("\"foo bar\" | match(\"o\")", "{\"offset\":1,\"length\":1,\"string\":\"o\",\"captures\":[]}\n")]
    [InlineData("\"foo\" | [match(\"o\"; \"g\")]", "[{\"offset\":1,\"length\":1,\"string\":\"o\",\"captures\":[]},{\"offset\":2,\"length\":1,\"string\":\"o\",\"captures\":[]}]\n")]
    [InlineData("\"abc\" | [match(\"( )*\"; \"g\")]", "[{\"offset\":0,\"length\":0,\"string\":\"\",\"captures\":[{\"offset\":-1,\"length\":0,\"string\":null,\"name\":null}]},{\"offset\":1,\"length\":0,\"string\":\"\",\"captures\":[{\"offset\":-1,\"length\":0,\"string\":null,\"name\":null}]},{\"offset\":2,\"length\":0,\"string\":\"\",\"captures\":[{\"offset\":-1,\"length\":0,\"string\":null,\"name\":null}]},{\"offset\":3,\"length\":0,\"string\":\"\",\"captures\":[{\"offset\":-1,\"length\":0,\"string\":null,\"name\":null}]}]\n")]
    [InlineData("\"abc\" | [match(\"( )*\"; \"gn\")]", "[]\n")]
    [InlineData("\"ab\" | [match(\"\"; \"g\")]", "[{\"offset\":0,\"length\":0,\"string\":\"\",\"captures\":[]},{\"offset\":1,\"length\":0,\"string\":\"\",\"captures\":[]},{\"offset\":2,\"length\":0,\"string\":\"\",\"captures\":[]}]\n")]
    [InlineData("\"ab\" | match(\"\")", "{\"offset\":0,\"length\":0,\"string\":\"\",\"captures\":[]}\n")]
    [InlineData("\"foo bar\" | [match([\"(bar)\"])]", "[{\"offset\":4,\"length\":3,\"string\":\"bar\",\"captures\":[{\"offset\":4,\"length\":3,\"string\":\"bar\",\"name\":null}]}]\n")]
    [InlineData("\"a\\u0304 bar\" | [match(\"bar\")]", "[{\"offset\":3,\"length\":3,\"string\":\"bar\",\"captures\":[]}]\n")]
    [InlineData("\"a ba\\u0304r\" | [match(\"ba\\u0304r\")]", "[{\"offset\":2,\"length\":4,\"string\":\"ba\u0304r\",\"captures\":[]}]\n")]
    [InlineData("\"foo bar foo foo  foo\" | [match([\"foo (?<bar123>bar)? foo\", \"ig\"])]", "[{\"offset\":0,\"length\":11,\"string\":\"foo bar foo\",\"captures\":[{\"offset\":4,\"length\":3,\"string\":\"bar\",\"name\":\"bar123\"}]},{\"offset\":12,\"length\":8,\"string\":\"foo  foo\",\"captures\":[{\"offset\":-1,\"length\":0,\"string\":null,\"name\":\"bar123\"}]}]\n")]
    [InlineData("\"a\",\"b\",\"c\" | match(\"(?<x>a)?b?\")", "{\"offset\":0,\"length\":1,\"string\":\"a\",\"captures\":[{\"offset\":0,\"length\":1,\"string\":\"a\",\"name\":\"x\"}]}\n{\"offset\":0,\"length\":1,\"string\":\"b\",\"captures\":[{\"offset\":-1,\"length\":0,\"string\":null,\"name\":\"x\"}]}\n{\"offset\":0,\"length\":0,\"string\":\"\",\"captures\":[{\"offset\":-1,\"length\":0,\"string\":null,\"name\":\"x\"}]}\n")]
    [InlineData("\"xyzzy-14\" | capture(\"(?<a>[a-z]+)-(?<n>[0-9]+)\")", "{\"a\":\"xyzzy\",\"n\":\"14\"}\n")]
    [InlineData("\"a\",\"b\",\"c\" | capture(\"(?<x>a)?b?\")", "{\"x\":\"a\"}\n{\"x\":null}\n{\"x\":null}\n")]
    [InlineData("\"a\",\"b\",\"c\" | capture(\"(?<x>a?)?b?\")", "{\"x\":\"a\"}\n{\"x\":\"\"}\n{\"x\":\"\"}\n")]
    [InlineData("\"2026-09-29\" | capture(\"(?<y>\\\\d+)-(\\\\d+)-(?<d>\\\\d+)\")", "{\"y\":\"2026\",\"d\":\"29\"}\n")]
    [InlineData("\"a1 b2\" | [capture(\"(?<c>[a-z])(?<n>[0-9])\"; \"g\")]", "[{\"c\":\"a\",\"n\":\"1\"},{\"c\":\"b\",\"n\":\"2\"}]\n")]
    [InlineData("\"é🚀x\" | match(\"x\") | .offset", "2\n")]
    [InlineData("\"é🚀\" | match(\"🚀\")", "{\"offset\":1,\"length\":1,\"string\":\"\\uD83D\\uDE80\",\"captures\":[]}\n")]
    [InlineData("\"ab\" | [match(\"b*\"; \"g\")] | map([.offset, .length])", "[[0,0],[1,1],[2,0]]\n")]
    [InlineData("\"qux\" | [match(\"(?=u)\"; \"g\")] | map(.offset)", "[1]\n")]
    // Empty global matches advance by scalar; the 1.8.1 byte-wise quirk is intentionally not kept.
    [InlineData("\"🚀\" | [match(\"\"; \"g\")] | map(.offset)", "[0,1]\n")]
    [InlineData("\"ba\" | match(\"a*\") | [.offset, .length]", "[0,0]\n")]
    [InlineData("\"foo\" | match([\"o\"]) | .offset", "1\n")]
    [InlineData("\"foo\" | [match([\"o\", \"g\"]) | .offset]", "[1,2]\n")]
    [InlineData("\"a\\nb\" | match(\"a.b\"; \"s\") | .offset", "0\n")]
    [InlineData("\"a\\nb\" | match(\"a.b\"; \"m\")", "")]
    [InlineData("\"a\\nb\" | match(\"^a.b$\"; \"p\") | .offset", "0\n")]
    [InlineData("\"AB\" | match(\"ab\"; \"i\") | .string", "\"AB\"\n")]
    public async Task Jq_MatchCaptureVectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("5 | match(\"x\")", "number (5) cannot be matched, as it is not a string")]
    [InlineData("\"x\" | match(\"x\"; 5)", "number (5) is not a string")]
    [InlineData("\"x\" | match(\"x\"; \"z\")", "z is not a valid modifier string")]
    [InlineData("5 | capture(\"x\")", "number (5) cannot be matched, as it is not a string")]
    [InlineData("\"x\" | capture(5)", "number not a string or array")]
    [InlineData("\"x\" | match(5)", "error: number not a string or array")]
    public async Task Jq_MatchCaptureFailures(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_MatchValueDispatchErrorCarriesPayload()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try (\"x\" | match(5)) catch .")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"number not a string or array\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"x\" | match(\"a\";\"g\";\"x\")", "expects one or two")]
    [InlineData("\"x\" | capture", "expects one or two")]
    public async Task Jq_MatchCaptureArityIsCompileError(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
