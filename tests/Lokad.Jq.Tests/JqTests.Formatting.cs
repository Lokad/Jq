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
    [InlineData("null", "null (null) cannot be tsv-formatted, only array")]
    [InlineData("\"text\"", "string (\"text\") cannot be tsv-formatted, only array")]
    [InlineData("{}", "object ({}) cannot be tsv-formatted, only array")]
    [InlineData("[\"valid\", []]", "array ([]) is not valid in a csv row")]
    [InlineData("[{}]", "object ({}) is not valid in a csv row")]
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

    [Theory]
    [InlineData("\"\"", "@base64", "\"\"\n")]
    [InlineData("\"<>&'\\\"\\t\"", "@base64", "\"PD4mJyIJ\"\n")]
    [InlineData("\"foóbar\\n\"", "@base64", "\"Zm/Ds2Jhcgo=\"\n")]
    [InlineData("\"<>&'\\\"\\t\"", "@base64 | @base64d", "\"<>&'\\\"\\t\"\n")]
    [InlineData("\"\"", "@base64d", "\"\"\n")]
    [InlineData("\"=\"", "@base64d", "\"\"\n")]
    [InlineData("\"Zm/Ds2Jhcgo=\"", "@base64d", "\"foóbar\\n\"\n")]
    [InlineData("\"cWl4YmF6Cg\"", "@base64d", "\"qixbaz\\n\"\n")]
    [InlineData("\"AB=C\"", "@base64d", "\"\\u0000\"\n")]
    public async Task Jq_Base64Vectors(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"\\b\\t\\n\\f\\r\\\"\\\\\"", "@uri", "\"%08%09%0A%0C%0D%22%5C\"\n")]
    [InlineData("\",-./09:;<=>?@AZ[\\\\]^_`az{|}~\u007f\"", "@uri", "\"%2C-.%2F09%3A%3B%3C%3D%3E%3F%40AZ%5B%5C%5D%5E_%60az%7B%7C%7D~%7F\"\n")]
    [InlineData("\"a \\u03bc \\u2230 \\ud83d\\ude0e\"", "@uri", "\"a%20%CE%BC%20%E2%88%B0%20%F0%9F%98%8E\"\n")]
    [InlineData("\"a\\u0000b\\u0000c\"", "@uri", "\"a%00b%00c\"\n")]
    [InlineData("\"%08%09%0A%0C%0D%22%5C\"", "@urid", "\"\\b\\t\\n\\f\\r\\\"\\\\\"\n")]
    [InlineData("\"a%20%C3%A9\"", "@urid", "\"a é\"\n")]
    [InlineData("\"hello world\"", "@urid", "\"hello world\"\n")]
    [InlineData("\"Knäckebröd\"", "@urid", "\"Knäckebröd\"\n")]
    [InlineData("\"a%00b\"", "@urid", "\"a\\u0000b\"\n")]
    public async Task Jq_UriVectors(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"!()<>&'\\\"\\t\"", "@html", "\"!()&lt;&gt;&amp;&apos;&quot;\\t\"\n")]
    [InlineData("\"This works if x < y\"", "@html", "\"This works if x &lt; y\"\n")]
    [InlineData("\"O'Hara's Ale\"", "@sh", "\"'O'\\\\''Hara'\\\\''s Ale'\"\n")]
    [InlineData("[\"a\",\"b c\",1,true,null]", "@sh", "\"'a' 'b c' 1 true null\"\n")]
    [InlineData("[\"a\",\"b\"]", "@csv", "\"\\\"a\\\",\\\"b\\\"\"\n")]
    [InlineData("[1,\"a,b\",\"c\\\"d\",null,true,1.5]", "@csv", "\"1,\\\"a,b\\\",\\\"c\\\"\\\"d\\\",,true,1.5\"\n")]
    [InlineData("[1,\"a\",\"b c\"]", "@tsv", "\"1\\ta\\tb c\"\n")]
    public async Task Jq_HtmlShellCsvVectors(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"a/b\" | @uri \"x=\\(.)\"", "x=a%2Fb\n")]
    [InlineData("[\"a\"] | @sh \"run \\(.[0])\"", "run 'a'\n")]
    [InlineData("\"<t>\" | @html \"<b>\\(.)</b>\"", "<b>&lt;t&gt;</b>\n")]
    public async Task Jq_FormatTemplatesEncodeOnlyValues(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"Not base64 data\"", "@base64d", "string (\"Not base64 data\") is not valid base64 data")]
    [InlineData("\"QUJDa\"", "@base64d", "string (\"QUJDa\") trailing base64 byte found")]
    [InlineData("\"abc%\"", "@urid", "string (\"abc%\") is not a valid uri encoding")]
    [InlineData("\"abc%f\"", "@urid", "string (\"abc%f\") is not a valid uri encoding")]
    [InlineData("\"abc%g\"", "@urid", "string (\"abc%g\") is not a valid uri encoding")]
    [InlineData("\"%F0%93%81\"", "@urid", "string (\"%F0%93%81\") is not a valid uri encoding")]
    [InlineData("\"%F0%C0%81%8E\"", "@urid", "string (\"%F0%C0%81%8E\") is not a valid uri encoding")]
    [InlineData("[\"ok\",[1]]", "@sh", "array ([1]) can not be escaped for shell")]
    [InlineData("5", "@csv", "number (5) cannot be csv-formatted, only array")]
    [InlineData("[\"ok\",{}]", "@csv", "object ({}) is not valid in a csv row")]
    [InlineData("5", "@tsv", "number (5) cannot be tsv-formatted, only array")]
    public async Task Jq_UpstreamFormatFailures(string input, string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}

