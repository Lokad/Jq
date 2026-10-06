using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    // Oracle vectors: external/jq tests/onig.test, compared by value upstream.
    // Capture key order below is uniform (offset, length, string, name).
    [InlineData("\"foo bar\" | match(\"o\")", "{\n  \"offset\": 1,\n  \"length\": 1,\n  \"string\": \"o\",\n  \"captures\": []\n}\n")]
    [InlineData("\"foo\" | [match(\"o\"; \"g\")]", "[\n  {\n    \"offset\": 1,\n    \"length\": 1,\n    \"string\": \"o\",\n    \"captures\": []\n  },\n  {\n    \"offset\": 2,\n    \"length\": 1,\n    \"string\": \"o\",\n    \"captures\": []\n  }\n]\n")]
    [InlineData("\"abc\" | [match(\"( )*\"; \"g\")]", "[\n  {\n    \"offset\": 0,\n    \"length\": 0,\n    \"string\": \"\",\n    \"captures\": [\n      {\n        \"offset\": -1,\n        \"length\": 0,\n        \"string\": null,\n        \"name\": null\n      }\n    ]\n  },\n  {\n    \"offset\": 1,\n    \"length\": 0,\n    \"string\": \"\",\n    \"captures\": [\n      {\n        \"offset\": -1,\n        \"length\": 0,\n        \"string\": null,\n        \"name\": null\n      }\n    ]\n  },\n  {\n    \"offset\": 2,\n    \"length\": 0,\n    \"string\": \"\",\n    \"captures\": [\n      {\n        \"offset\": -1,\n        \"length\": 0,\n        \"string\": null,\n        \"name\": null\n      }\n    ]\n  },\n  {\n    \"offset\": 3,\n    \"length\": 0,\n    \"string\": \"\",\n    \"captures\": [\n      {\n        \"offset\": -1,\n        \"length\": 0,\n        \"string\": null,\n        \"name\": null\n      }\n    ]\n  }\n]\n")]
    [InlineData("\"abc\" | [match(\"( )*\"; \"gn\")]", "[]\n")]
    [InlineData("\"ab\" | [match(\"\"; \"g\")]", "[\n  {\n    \"offset\": 0,\n    \"length\": 0,\n    \"string\": \"\",\n    \"captures\": []\n  },\n  {\n    \"offset\": 1,\n    \"length\": 0,\n    \"string\": \"\",\n    \"captures\": []\n  },\n  {\n    \"offset\": 2,\n    \"length\": 0,\n    \"string\": \"\",\n    \"captures\": []\n  }\n]\n")]
    [InlineData("\"ab\" | match(\"\")", "{\n  \"offset\": 0,\n  \"length\": 0,\n  \"string\": \"\",\n  \"captures\": []\n}\n")]
    [InlineData("\"foo bar\" | [match([\"(bar)\"])]", "[\n  {\n    \"offset\": 4,\n    \"length\": 3,\n    \"string\": \"bar\",\n    \"captures\": [\n      {\n        \"offset\": 4,\n        \"length\": 3,\n        \"string\": \"bar\",\n        \"name\": null\n      }\n    ]\n  }\n]\n")]
    [InlineData("\"abc abc\" | match(\"(abc)+\"; \"g\")", "{\n  \"offset\": 0,\n  \"length\": 3,\n  \"string\": \"abc\",\n  \"captures\": [\n    {\n      \"offset\": 0,\n      \"length\": 3,\n      \"string\": \"abc\",\n      \"name\": null\n    }\n  ]\n}\n{\n  \"offset\": 4,\n  \"length\": 3,\n  \"string\": \"abc\",\n  \"captures\": [\n    {\n      \"offset\": 4,\n      \"length\": 3,\n      \"string\": \"abc\",\n      \"name\": null\n    }\n  ]\n}\n")]
    [InlineData("\"foo bar FOO\" | match([\"foo\", \"ig\"])", "{\n  \"offset\": 0,\n  \"length\": 3,\n  \"string\": \"foo\",\n  \"captures\": []\n}\n{\n  \"offset\": 8,\n  \"length\": 3,\n  \"string\": \"FOO\",\n  \"captures\": []\n}\n")]
    [InlineData("\"foo bar foo foo  foo\" | match(\"foo (?<bar123>bar)? foo\"; \"ig\")", "{\n  \"offset\": 0,\n  \"length\": 11,\n  \"string\": \"foo bar foo\",\n  \"captures\": [\n    {\n      \"offset\": 4,\n      \"length\": 3,\n      \"string\": \"bar\",\n      \"name\": \"bar123\"\n    }\n  ]\n}\n{\n  \"offset\": 12,\n  \"length\": 8,\n  \"string\": \"foo  foo\",\n  \"captures\": [\n    {\n      \"offset\": -1,\n      \"length\": 0,\n      \"string\": null,\n      \"name\": \"bar123\"\n    }\n  ]\n}\n")]
    [InlineData("\"abc\" | [ match(\".\"; \"g\")] | length", "3\n")]
    [InlineData("\"a\\u0304 bar\" | [match(\"bar\")]", "[\n  {\n    \"offset\": 3,\n    \"length\": 3,\n    \"string\": \"bar\",\n    \"captures\": []\n  }\n]\n")]
    [InlineData("\"a ba\\u0304r\" | [match(\"ba\\u0304r\")]", "[\n  {\n    \"offset\": 2,\n    \"length\": 4,\n    \"string\": \"bār\",\n    \"captures\": []\n  }\n]\n")]
    [InlineData("\"foo bar foo foo  foo\" | [match([\"foo (?<bar123>bar)? foo\", \"ig\"])]", "[\n  {\n    \"offset\": 0,\n    \"length\": 11,\n    \"string\": \"foo bar foo\",\n    \"captures\": [\n      {\n        \"offset\": 4,\n        \"length\": 3,\n        \"string\": \"bar\",\n        \"name\": \"bar123\"\n      }\n    ]\n  },\n  {\n    \"offset\": 12,\n    \"length\": 8,\n    \"string\": \"foo  foo\",\n    \"captures\": [\n      {\n        \"offset\": -1,\n        \"length\": 0,\n        \"string\": null,\n        \"name\": \"bar123\"\n      }\n    ]\n  }\n]\n")]
    [InlineData("\"a\",\"b\",\"c\" | match(\"(?<x>a)?b?\")", "{\n  \"offset\": 0,\n  \"length\": 1,\n  \"string\": \"a\",\n  \"captures\": [\n    {\n      \"offset\": 0,\n      \"length\": 1,\n      \"string\": \"a\",\n      \"name\": \"x\"\n    }\n  ]\n}\n{\n  \"offset\": 0,\n  \"length\": 1,\n  \"string\": \"b\",\n  \"captures\": [\n    {\n      \"offset\": -1,\n      \"length\": 0,\n      \"string\": null,\n      \"name\": \"x\"\n    }\n  ]\n}\n{\n  \"offset\": 0,\n  \"length\": 0,\n  \"string\": \"\",\n  \"captures\": [\n    {\n      \"offset\": -1,\n      \"length\": 0,\n      \"string\": null,\n      \"name\": \"x\"\n    }\n  ]\n}\n")]
    [InlineData("\"xyzzy-14\" | capture(\"(?<a>[a-z]+)-(?<n>[0-9]+)\")", "{\n  \"a\": \"xyzzy\",\n  \"n\": \"14\"\n}\n")]
    [InlineData("\"a\",\"b\",\"c\" | capture(\"(?<x>a)?b?\")", "{\n  \"x\": \"a\"\n}\n{\n  \"x\": null\n}\n{\n  \"x\": null\n}\n")]
    [InlineData("\"a\",\"b\",\"c\" | capture(\"(?<x>a?)?b?\")", "{\n  \"x\": \"a\"\n}\n{\n  \"x\": \"\"\n}\n{\n  \"x\": \"\"\n}\n")]
    [InlineData("\"2026-09-29\" | capture(\"(?<y>\\\\d+)-(\\\\d+)-(?<d>\\\\d+)\")", "{\n  \"y\": \"2026\",\n  \"d\": \"29\"\n}\n")]
    [InlineData("\"a1 b2\" | [capture(\"(?<c>[a-z])(?<n>[0-9])\"; \"g\")]", "[\n  {\n    \"c\": \"a\",\n    \"n\": \"1\"\n  },\n  {\n    \"c\": \"b\",\n    \"n\": \"2\"\n  }\n]\n")]
    [InlineData("\"é🚀x\" | match(\"x\") | .offset", "2\n")]
    [InlineData("\"é🚀\" | match(\"🚀\")", "{\n  \"offset\": 1,\n  \"length\": 1,\n  \"string\": \"\\ud83d\\ude80\",\n  \"captures\": []\n}\n")]
    [InlineData("\"ab\" | [match(\"b*\"; \"g\")] | map([.offset, .length])", "[\n  [\n    0,\n    0\n  ],\n  [\n    1,\n    1\n  ],\n  [\n    2,\n    0\n  ]\n]\n")]
    [InlineData("\"qux\" | [match(\"(?=u)\"; \"g\")] | map(.offset)", "[\n  1\n]\n")]
    // Empty global matches advance by scalar; the 1.8.1 byte-wise quirk is intentionally not kept.
    [InlineData("\"🚀\" | [match(\"\"; \"g\")] | map(.offset)", "[\n  0,\n  1\n]\n")]
    [InlineData("\"ba\" | match(\"a*\") | [.offset, .length]", "[\n  0,\n  0\n]\n")]
    [InlineData("\"foo\" | match([\"o\"]) | .offset", "1\n")]
    [InlineData("\"foo\" | [match([\"o\", \"g\"]) | .offset]", "[\n  1,\n  2\n]\n")]
    [InlineData("\"a\\nb\" | match(\"a.b\"; \"s\") | .offset", "0\n")]
    [InlineData("\"a\\nb\" | match(\"a.b\"; \"m\")", "")]
    [InlineData("\"a\\nb\" | match(\"^a.b$\"; \"p\") | .offset", "0\n")]
    [InlineData("\"AB\" | match(\"ab\"; \"i\") | .string", "\"AB\"\n")]
    [InlineData("\"\\u0101\\u00e1\\u00e0\\u00e4\" | [match(\"a\"; \"gi\")]", "[]\n")]
    [InlineData("\"a\\u0304 two-codepoint grapheme\" | [match(\".+?\\\\b\")]", "[\n  {\n    \"offset\": 0,\n    \"length\": 2,\n    \"string\": \"ā\",\n    \"captures\": []\n  }\n]\n")]
    [InlineData("\"a\",\"b\",\"c\" | match(\"(?<x>a?)?b?\")", "{\n  \"offset\": 0,\n  \"length\": 1,\n  \"string\": \"a\",\n  \"captures\": [\n    {\n      \"offset\": 0,\n      \"length\": 1,\n      \"string\": \"a\",\n      \"name\": \"x\"\n    }\n  ]\n}\n{\n  \"offset\": 0,\n  \"length\": 1,\n  \"string\": \"b\",\n  \"captures\": [\n    {\n      \"offset\": 0,\n      \"length\": 0,\n      \"string\": \"\",\n      \"name\": \"x\"\n    }\n  ]\n}\n{\n  \"offset\": 0,\n  \"length\": 0,\n  \"string\": \"\",\n  \"captures\": [\n    {\n      \"offset\": 0,\n      \"length\": 0,\n      \"string\": \"\",\n      \"name\": \"x\"\n    }\n  ]\n}\n")]
    [InlineData("\"foo bar foo\" | match(\"foo\")", "{\n  \"offset\": 0,\n  \"length\": 3,\n  \"string\": \"foo\",\n  \"captures\": []\n}\n")]
    [InlineData("\"ab\" | capture(\"(?<x>a)(?<x>b)\")", "{\n  \"x\": \"b\"\n}\n")]
    [InlineData("\"ab\" | capture(\"(?<x>a)|(?<x>b)\")", "{\n  \"x\": null\n}\n")]
    [InlineData("\"b\" | capture(\"(?<x>a)|(?<x>b)\")", "{\n  \"x\": \"b\"\n}\n")]
    [InlineData("\"ab\" | match(\"(?<x>a)|(?<x>b)\")", "{\n  \"offset\": 0,\n  \"length\": 1,\n  \"string\": \"a\",\n  \"captures\": [\n    {\n      \"offset\": 0,\n      \"length\": 1,\n      \"string\": \"a\",\n      \"name\": \"x\"\n    },\n    {\n      \"offset\": -1,\n      \"length\": 0,\n      \"string\": null,\n      \"name\": \"x\"\n    }\n  ]\n}\n")]
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
    [InlineData("\"x\" | match(5)", "number not a string or array")]
    [InlineData("\"x\" | match(null)", "null not a string or array")]
    [InlineData("\"x\" | capture(null)", "null not a string or array")]



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
