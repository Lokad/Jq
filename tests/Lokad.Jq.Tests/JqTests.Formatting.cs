using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[\"ticket\", \"Open\"] | @tsv", "ticket\tOpen\n")]
    [InlineData("[\"a\", \"b\"] | @tsv | length", "3\n")]
    [InlineData("[\"a\"] | @tsv + \"!\"", "a!\n")]
    [InlineData("[\"a\"] | [@tsv, @tsv] | join(\"|\")", "a|a\n")]
    [InlineData("[\"a\"] | if true then @tsv else @json end", "a\n")]
    [InlineData("[\"a\"] | (@tsv)[0:1]", "a\n")]
    [InlineData("42 | @text", "42\n")]
    [InlineData("{a:1} | @json", "{\"a\":1}\n")]
    [InlineData("\"<tag>\" | @html", "&lt;tag&gt;\n")]
    [InlineData("\"Lokad\" | @base64 | @base64d", "Lokad\n")]
    public async Task Jq_StandaloneFormatsUseCurrentInput(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[]", "\n")]
    [InlineData("[null,\"\",true,false,1.5]", "\t\ttrue\tfalse\t1.5\n")]
    [InlineData("[\"a\\tb\",\"c\\nd\",\"e\\rf\",\"g\\\\h\",\"é🚀\",\"\\\"q\\\"\"]",
        "a\\tb\tc\\nd\te\\rf\tg\\\\h\té🚀\t\"q\"\n")]
    public async Task Jq_TsvPreservesFieldsOnOneLine(string input, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "@tsv")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TsvRendersProjectedRows()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("""[{"Title":"First\nrequest","Status":"Open"},{"Title":"Second","Status":null}]""");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", ".[] | [.Title,.Status] | @tsv")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("First\\nrequest\tOpen\nSecond\t\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("null", "@tsv requires an array")]
    [InlineData("\"text\"", "@tsv requires an array")]
    [InlineData("{}", "@tsv requires an array")]
    [InlineData("[\"valid\", []]", "@tsv cannot format array as a field")]
    [InlineData("[{}]", "@tsv cannot format object as a field")]
    public async Task Jq_TsvRejectsInvalidRows(string input, string error)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "@tsv")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal($"jq: {error}\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("@", 3, "expected Identifier")]
    [InlineData("@missing", 5, "unsupported format @missing")]
    [InlineData("@tsv |", 3, "unexpected token <end>")]
    public async Task Jq_InvalidFormatsReportToolErrors(string filter, int expectedExit, string error)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(expectedExit, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(error, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("null | @html \"<b>literal</b>\"", "<b>literal</b>\n")]
    [InlineData("\"<tag>\" | @html \"<b>\\(.)</b>\"", "<b>&lt;tag&gt;</b>\n")]
    [InlineData("[\"a\\tb\",2] | @tsv \"row: \\(.)\"", "row: a\\tb\t2\n")]
    public async Task Jq_FormatTemplatesEscapeOnlyInterpolations(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
