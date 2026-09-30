using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("-c")]
    [InlineData("--compact-output")]
    public async Task Jq_CompactOutputWritesOneJsonValuePerLine(string option)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("{\n  \"items\": [1, 2]\n}\n{\"text\":\"a\\nb\"}");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", option, ".")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("{\"items\":[1,2]}\n{\"text\":\"a\\nb\"}\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("-nc", "{a:1}", "{\"a\":1}\n")]
    [InlineData("-cn", "{a:1}", "{\"a\":1}\n")]
    [InlineData("-ncr", "\"hello\", {a:1}", "hello\n{\"a\":1}\n")]
    [InlineData("-ncrc", "\"hello\", {a:1}", "hello\n{\"a\":1}\n")]
    [InlineData("-ncj", "{a:1}, {a:2}", "{\"a\":1}{\"a\":2}")]
    [InlineData("-nca", "{text:\"é\"}", "{\"text\":\"\\u00E9\"}\n")]
    public async Task Jq_CompactOutputCombinesWithOtherFlags(string options, string filter, string expected)
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", options, filter)));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("", new[] { "-c" })]
    [InlineData("  ", new string[] { })]
    [InlineData("", new[] { "--indent", "4", "-c" })]
    [InlineData("", new[] { "--tab", "--compact-output" })]
    [InlineData("", new[] { "--indent=4", "-cr" })]
    [InlineData("    ", new[] { "-c", "--indent", "4" })]
    [InlineData("    ", new[] { "--tab", "-c", "--indent=4" })]
    [InlineData("\t", new[] { "--compact-output", "--tab" })]
    [InlineData("\t", new[] { "--indent=4", "-c", "--tab" })]
    [InlineData("", new[] { "-c", "--tab", "-c" })]
    [InlineData("", new[] { "-c", "--compact-output" })]
    [InlineData("", new[] { "-cc" })]
    [InlineData("\t", new[] { "--tab", "-c", "--tab" })]
    [InlineData("  ", new[] { "-c", "--indent=4", "-c", "--indent", "2" })]
    public async Task Jq_LastFormattingOptionWins(string indentation, string[] options)
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ["-n", .. options, "{a:1}"])));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        var expected = indentation.Length == 0
            ? "{\"a\":1}\n"
            : "{" + "\n" + indentation + "\"a\": 1" + "\n" + "}\n";
        Assert.Equal(0, exitCode);
        Assert.Equal(expected, fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("--arg", "text", "-c", "{a:1}")]
    [InlineData("--", "{a:1}", "-c")]
    [InlineData("-f=c")]
    public async Task Jq_CompactOutputSpellingInOperandsDoesNotChangeFormatting(params string[] arguments)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("null");
        fileSystem.AddFile("/-c", "null");
        fileSystem.AddFile("/c", "{a:1}");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ["--tab", .. arguments])));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("{" + "\n" + "\t\"a\": 1" + "\n" + "}\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("--compact-output=true")]
    [InlineData("--tab=true")]
    [InlineData("--indent=bad")]
    [InlineData("--indent=9")]
    public async Task Jq_CompactOutputDoesNotHideInvalidFormattingOptions(string option)
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", option, "-c", "{a:1}")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.NotEmpty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("--indent", "-c", "2")]
    [InlineData("--indent", "--tab", "2")]
    [InlineData("-nf", "-c", "/filter.jq")]
    [InlineData("-ctab", "{a:1}")]
    [InlineData("-cversion", "{a:1}")]
    [InlineData("-cindent", "2", "{a:1}")]
    public async Task Jq_FormattingNormalizationPreservesInvalidArgumentBoundaries(params string[] arguments)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile("/filter.jq", "{a:1}");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.NotEmpty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("-ncf", "/filter.jq")]
    [InlineData("-nfc", "/filter.jq")]
    [InlineData("-nccff", "/unused.jq", "/filter.jq")]
    public async Task Jq_CompactOutputPreservesGroupedFilterFileOperands(params string[] arguments)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile("/filter.jq", "{a:1}");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("{\"a\":1}\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }
}


