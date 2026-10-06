using Lokad.Jq;
using Lokad.Utf8Regex.Pcre2;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"<p>Hello&nbsp;  world</p>\" | gsub(\"<[^>]+>\";\" \") | gsub(\"&nbsp;\";\" \") | gsub(\"[[:space:]]+\";\" \")", " Hello world \n")]
    [InlineData("\"é 🚀 世界\" | gsub(\".\"; \"X\")", "XXXXXX\n")]
    [InlineData("\"a\\r\\nb\\t\\u00a0c\" | gsub(\"[[:space:]]+\"; \" \")", "a b c\n")]
    [InlineData("\"Abcabc\" | gsub(\"(?<x>.)[^a]*\"; \"+\\(.x)-\")", "+A-+a-\n")]
    [InlineData("\"b\" | gsub(\"(?<x>a)?b\"; .x // \"missing\")", "missing\n")]
    [InlineData("\"ab\" | gsub(\".\"; \"x\", \"y\")", "xx\nyy\n")]
    [InlineData("\"ab\" | gsub(\"(?<x>.)\"; if .x == \"a\" then \"1\", \"2\" else \"3\" end)", "13\n2\n")]
    [InlineData("\"ab\" | gsub(\"(?<x>.)\"; if .x == \"a\" then \"1\" else \"2\", \"3\" end)", "12\n3\n")]
    [InlineData("\"ab\" | gsub(\"a\"; empty)", "ab\n")]
    [InlineData("\"ab\" | gsub(\"a\"; null)", "b\n")]
    [InlineData("\"ab\" | gsub(\"x\"; 1)", "ab\n")]
    [InlineData("\"ab\" | gsub(\"a*\"; \"X\")", "XXbX\n")]
    [InlineData("\"abc\" | gsub(\".*\"; \"X\")", "XX\n")]
    // Advance by Unicode scalar; jq 1.8.1 repeats empty matches inside multibyte characters.
    [InlineData("\"é🚀\" | gsub(\"\"; \"X\")", "XéX🚀X\n")]
    [InlineData("\"\" | gsub(\"\"; \"X\")", "X\n")]
    [InlineData("\"ab\" | gsub(\"a*\"; \"X\"; \"n\")", "Xb\n")]
    [InlineData("\"aA\" | gsub(\"a\"; \"X\"; \"ig\")", "XX\n")]
    [InlineData("\"a\\nb\\n\" | gsub(\"^\"; \"X\")", "Xa\nb\n\n")]
    [InlineData("\"a\\nb\\n\" | gsub(\"$\"; \"X\"; \"s\")", "a\nbX\nX\n")]
    [InlineData("\"a\\nb\" | gsub(\".\"; \"X\"; \"m\")", "X\nX\n")]
    [InlineData("\"a\\nb\" | gsub(\".\"; \"X\"; \"p\")", "XXX\n")]
    [InlineData("\"ab\" | gsub(\"a b # comment\"; \"X\"; \"x\")", "X\n")]
    [InlineData("\"a\" | gsub(\"a\"; \"$1\\\\\"; null)", "$1\\\n")]
    [InlineData("\"ab\" | gsub((\"a\", \"b\"); \"X\")", "Xb\naX\n")]
    [InlineData("\"aA\" | gsub(\"a\"; \"X\"; (\"\", \"i\"))", "XA\nXX\n")]
    [InlineData("\"ab\" | gsub(\"(?<x>.)\"; .x | gsub(\"(?<x>.)\"; \"z\"))", "zz\n")]
    [InlineData("\"A1 B2 CD\" | gsub(\"(?<x>.)(?<y>[0-9])\"; \"\\(.x|ascii_downcase)\\(.y)\")", "a1 b2 CD\n")]
    [InlineData("\"ABC DEF\" | gsub(\"\\\\b(?<x>.)\"; \"\\(.x|ascii_downcase)\")", "aBC dEF\n")]
    [InlineData("\"123foo456bar\" | gsub(\"[^a-z]*(?<x>[a-z]*)\"; \"Z\\(.x)\")", "ZfooZbarZ\n")]
    [InlineData("\"aB\" | [gsub(\"(?<a>.)\"; \"\\(.a|ascii_upcase)\", \"\\(.a|ascii_downcase)\", \"c\")]", "[\n  \"AB\",\n  \"ab\",\n  \"cc\"\n]\n")]
    [InlineData("\"a\" | gsub(\"^\"; \"\"; \"g\")", "a\n")]
    [InlineData("\"a\" | gsub(\"\"; \"a\"; \"g\")", "aaa\n")]
    [InlineData("\"a\" | gsub(\"$\"; \"a\"; \"g\")", "aa\n")]
    [InlineData("\"qux\" | gsub(\"(?=u)\"; \"u\")", "quux\n")]
    [InlineData("\"aaa\" | gsub(\"^.*a\"; \"b\")", "b\n")]
    [InlineData("\"aaa\" | gsub(\"^.*?a\"; \"b\")", "baa\n")]
    [InlineData("\"a1b2\" | gsub(\"(?<d>\\\\d)\"; \":\\(.d);\")", "a:1;b:2;\n")]
    [InlineData("\"aaaaa\" | gsub(\"a\";\"b\")", "bbbbb\n")]
    [InlineData("\"\" | gsub(\"(.*)\"; \"\"; \"x\")", "\n")]
    [InlineData("\"p\" | [gsub(\"p\"; \"a\", \"b\")]", "[\n  \"a\",\n  \"b\"\n]\n")]
[InlineData("[\"a,b, c, d, e,f\", \", a,b, c, d, e,f, \"] | [.[] | gsub(\", \"; \":\")]", "[\n  \"a,b:c:d:e,f\",\n  \":a,b:c:d:e,f:\"\n]\n")]
    [InlineData("\"\" | gsub(\"^\"; \"a\")", "a\n")]
    [InlineData("\"ab\" | [sub(\"(?<x>a)(?<x>b)\"; \"<\\(.x)>\")]", "[\n  \"<b>\"\n]\n")]
    [InlineData("\"ab\" | [gsub(\"(?<x>.)\"; \"[\\(.x)]\")]", "[\n  \"[a][b]\"\n]\n")]
    [InlineData("\"a\" | [gsub(\"a\"; \"b\", \"c\")]", "[\n  \"b\",\n  \"c\"\n]\n")]
    public async Task Jq_GsubPreservesReplacementSemantics(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_GsubCleansHtmlCommentsBeforeTsvFormatting()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("""{"Comments":[{"Created":"2026-09-15T12:00:00Z","BodyHtml":"<p>Sample&nbsp;  text</p>"}]}""");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            """.Comments[] | [.Created[0:10], (.BodyHtml | gsub("<[^>]*>";" ") | gsub("&nbsp;";" ") | gsub("[[:space:]]+";" "))] | @tsv""")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("2026-09-15\t Sample text \n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }


    [Theory]
    [InlineData("\"x\" | gsub", "expects two or three")]
    [InlineData("\"x\" | gsub(\"x\")", "expects two or three")]
    [InlineData("\"x\" | gsub(\"x\";\"y\";\"i\";\"g\")", "expects two or three")]
    public async Task Jq_GsubArityMismatchesAreCompileErrors(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("null | gsub(\"x\";\"y\")", "null (null) cannot be matched, as it is not a string")]
    [InlineData("\"x\" | gsub(1;\"y\")", "number (1) is not a string")]
    [InlineData("\"x\" | gsub(\"x\";1)", "expected string")]
    [InlineData("\"x\" | gsub(\"x\";\"y\";1)", "number (1) is not a string")]
    [InlineData("\"x\" | gsub(\"[\";\"y\")", "invalid regex")]
    [InlineData("\"x\" | gsub(\"x\";\"y\";\"l\")", "unsupported regex flag")]
    [InlineData("\"x\" | gsub(\"x\";\"y\";\"z\")", "z is not a valid modifier string")]
    [InlineData("\"x\" | gsub(\"x\" * 16385;\"y\")", "regex pattern exceeds")]
    [InlineData("\"🚀\" | gsub(\"\\\\C\";\"y\")", "invalid regex")]
    public async Task Jq_GsubReportsInvalidInputsAndRegexLimits(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
    [Theory]
    [InlineData("<p>x</p>", "gsub(\"<[^>]+>\";\" \") | gsub(\"&nbsp;\";\" \") | gsub(\"[[:space:]]+\";\" \")", " x ")]
    [InlineData("ab", "gsub(\"(?<x>.)\"; .x | gsub(\"(?<x>.)\"; \"y\"))", "yy")]
    public async Task Jq_GsubProcessesBatchesWithinItsMemoryBudget(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat($"\"{input}\"\n", 2000)));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat(expected + "\n", 2000)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public void Jq_RegexWorkAllowanceResetsForEachSearch()
    {
        using var regexes = new JqRegexCache(new JqBudget(new JqExecutionPolicy { MaximumRegexWork = 32 }, CancellationToken.None));
        var pattern = regexes.Get("x", Pcre2CompileOptions.None);
        for (int i = 0; i < 100; i++)
        {
            Assert.True(regexes.Match(pattern, "x"u8.ToArray(), 0, Pcre2MatchOptions.None).Success);
            Assert.True(regexes.IsMatch(pattern, "x"u8.ToArray(), Pcre2MatchOptions.None));
        }
    }

    [Fact]
    public void Jq_GsubRejectsInvalidUtf16BeforeMatching()
    {
        using var context = new JqContext(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(), JqProgramSource.Inline, new JqBudget(CancellationToken.None));
        var filter = new JqParser("gsub(\"a\";\"x\")", JqProgramSource.Inline, context.RootEnvironment, context.Budget).Parse();
        var input = System.Text.Json.Nodes.JsonValue.Create("a\ud800");

        var error = Assert.Throws<JqException>(() => filter.Evaluate(input, context, context.RootEnvironment).ToList());
        Assert.Contains("invalid regex input", error.Message);
    }
}
