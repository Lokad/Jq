using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    // Advanced constructs shared by the Oniguruma and PCRE2 Perl-compatible syntaxes.
    [InlineData("\"abab\" | match(\"(ab)\\\\1\") | .string", "\"abab\"\n")]
    [InlineData("\"abab\" | gsub(\"(ab)\\\\1\"; \"X\")", "\"X\"\n")]
    [InlineData("\"abab\" | match(\"(?<w>ab)\\\\k<w>\") | .offset", "0\n")]
    [InlineData("\"ab\" | match(\"(?<=a)b\") | .offset", "1\n")]
    [InlineData("\"ab\" | match(\"a(?!b)\")", "")]
    [InlineData("\"ac\" | match(\"a(?!b)\") | .offset", "0\n")]
    [InlineData("\"ab\" | match(\"(?<!a)b\")", "")]
    [InlineData("\"cb\" | match(\"(?<!a)b\") | .offset", "1\n")]
    [InlineData("\"aa\" | match(\"(?>a*)a\")", "")]
    [InlineData("\"aaa\" | match(\"a*+a\")", "")]
    [InlineData("\"ab\" | match(\"\\\\Aab\\\\z\") | .offset", "0\n")]
    [InlineData("\"foo bar\" | [match(\"\\\\b\\\\w+\"; \"g\")] | map(.string)", "[\n  \"foo\",\n  \"bar\"\n]\n")]
    [InlineData("\"ab\" | match(\"a|b\") | .offset", "0\n")]
    [InlineData("\"ba\" | match(\"a|b\") | .offset", "0\n")]
    [InlineData("\"aa\" | match(\"(a)*\") | .captures[0].string", "\"a\"\n")]
    [InlineData("\"abc\" | match(\"(a)?b(?(1)c|d)\") | .string", "\"abc\"\n")]
    [InlineData("\"bd\" | match(\"(a)?b(?(1)c|d)\") | .string", "\"bd\"\n")]
    [InlineData("\"AB\" | match(\"a b # comment\"; \"ix\") | .string", "\"AB\"\n")]
    [InlineData("\"a\\nb\" | [match(\"^.\"; \"mg\")] | map(.string)", "[\n  \"a\",\n  \"b\"\n]\n")]
    [InlineData("\"a1\" | match(\"[[:alpha:]][[:digit:]]\") | .offset", "0\n")]
    [InlineData("\"é1\" | match(\"\\\\p{L}+\") | .string", "\"é\"\n")]
    [InlineData("\"ab\" | [match(\".\"; \"g\") | .string] | .[] | match(\".\"; \"g\") | .offset", "0\n0\n")]
    [InlineData("\"ab\" | split(\".\")", "[\n  \"ab\"\n]\n")]
    [InlineData("\"ab\" | split(\".\"; \"\")", "[\n  \"\",\n  \"\",\n  \"\"\n]\n")]
    [InlineData("\"ab\" | capture(\"(?<x>.)(?<x>.)\")", "{\n  \"x\": \"b\"\n}\n")]
    public async Task Jq_RegexSyntaxVectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"ab\" | capture(\"(?<1>a)\")", "{}\n")]
    [InlineData("\"ab\" | match(\"(?<1>a)\") | [.captures[] | [.name, .string]]", "[\n  [\n    null,\n    \"a\"\n  ]\n]\n")]
    [InlineData("\"x\" | gsub(\"(\" * 65 + \"x\" + \")\" * 65; \"y\")", "y\n")]
    public async Task Jq_RegexEngineAdmissionVectors(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", filter)));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"x\" | match(\"[\")", "invalid regex")]
    [InlineData("\"aaa\" | match(\"a+\"; \"l\")", "unsupported regex flag")]
    [InlineData("\"aaa\" | test(\"a\"; \"l\")", "unsupported regex flag")]
    [InlineData("\"aaa\" | sub(\"a\"; \"X\"; \"l\")", "unsupported regex flag")]
    [InlineData("\"aaa\" | scan(\"a\"; \"l\")", "unsupported regex flag")]
    [InlineData("\"x\" | split(\"x\"; \"l\")", "unsupported regex flag")]
    [InlineData("\"x\" | capture(\"x\"; \"l\")", "unsupported regex flag")]
    [InlineData("\"aaa\" | splits(\"a\"; \"l\")", "unsupported regex flag")]
    [InlineData("\"x\" | gsub(\"x\"; \"y\"; \"q\")", "q is not a valid modifier string")]

    [InlineData("\"x\" | scan(\"x\"; \"q\")", "gq is not a valid modifier string")]
    [InlineData("\"x\" | splits(\"x\"; \"q\")", "qg is not a valid modifier string")]
    [InlineData("\"x\" | split(\"x\"; \"q\")", "qg is not a valid modifier string")]
    [InlineData("\"x\" | sub(\"x\"; \"y\"; \"q\")", "q is not a valid modifier string")]
    [InlineData("\"x\" | match(\"x\"; \"q\")", "q is not a valid modifier string")]
    [InlineData("\"x\" | capture(\"x\"; \"q\")", "q is not a valid modifier string")]
    [InlineData("\"x\" | scan(\"x\"; 1)", "number (1) is not a string")]
    [InlineData("\"x\" | split(\"x\"; 1)", "number (1) is not a string")]
    [InlineData("\"x\" | capture(\"x\"; 1)", "number (1) is not a string")]
    [InlineData("\"x\" | test(\"x\"; \"q\")", "q is not a valid modifier string")]
    public async Task Jq_RegexFlagLimits(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
