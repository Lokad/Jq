using System.Threading;
using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"ab\" * 4194305 | length")]
    [InlineData("(\"x\" * 8388608) + \"y\" | length")]
    [InlineData("[\"x\" * 4194304, \"y\" * 4194305] | add | length")]
    public async Task Jq_RejectsOversizedStringArithmetic(string filter)
    {
        // Each unguarded result would be at most 8 Mi characters plus two: keep probes bounded.
        var fileSystem = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var exitCode = await tool.ExecuteAsync(fileSystem, cancellation.Token);

        Assert.Equal(5, exitCode);
        Assert.Equal("jq: string result exceeds the 16 MiB UTF-16 limit\n", fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_RejectsRepeatedStringDoubling()
    {
        // Even without the limit, 24 doublings stop at 16 Mi characters.
        var filter = "\"x\" | " + string.Join(" | ", Enumerable.Repeat(". + .", 24)) + " | length";
        var fileSystem = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var exitCode = await tool.ExecuteAsync(fileSystem, cancellation.Token);

        Assert.Equal(5, exitCode);
        Assert.Contains("string result exceeds", fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("\"x\" * 8388608 | length", "8388608\n")]
    [InlineData("(\"ab\" * 2097152) + (\"cd\" * 2097152) | length", "8388608\n")]
    [InlineData("\"\U0001f680\" * 4194304 | length", "4194304\n")]
    public async Task Jq_AllowsStringArithmeticAtTheLimit(string filter, string expected)
    {
        var fileSystem = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var exitCode = await tool.ExecuteAsync(fileSystem, cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"ab\" * 3", "\"ababab\"\n")]
    [InlineData("\"é\U0001f680\" * 2", "\"é\\uD83D\\uDE80é\\uD83D\\uDE80\"\n")]
    [InlineData("\"same\" * 1", "\"same\"\n")]
    [InlineData("\"x\" * 0", "\"\"\n")]
    [InlineData("\"x\" * -2", "null\n")]
    [InlineData("\"\" * 1000000", "\"\"\n")]
    [InlineData("\"ab\" + \"cd\"", "\"abcd\"\n")]
    [InlineData("[2 * 3, 2 + 3, 2.5 * 4, -3 * 2]", "[\n  6,\n  5,\n  10,\n  -6\n]\n")]
    public async Task Jq_StringLimitsPreserveOrdinaryArithmetic(string filter, string expected)
    {
        var fileSystem = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FieldAccessReadsJsonFromStdin()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("""{"name":"Lokad","count":3}""");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".name")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.True(exitCode == 0, fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal("\"Lokad\"\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_RawOutputPrintsStringsWithoutJsonQuotes()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("""{"name":"Lokad"}""");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", ".name")));
        await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal("Lokad\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_NullInputBuildsObjectFromArg()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "--arg", "name", "Example", """{"name":$name}""")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.True(exitCode == 0, fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal("{\n  \"name\": \"Example\"\n}\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_ArgJsonAndArgsPopulateVariables()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation(
            "jq", "-n", "--argjson", "n", "3", """[$n, $ARGS.positional[]]""", "--jsonargs", "4", "5")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("[\n  3,\n  4,\n  5\n]\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_RawSlurpReadsWholeInputAsString()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("a\nb\n");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-R", "-s", ".")));
        await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal("\"a\\nb\\n\"\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_FilterFileReadsProgramThroughHost()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile("/filter.jq", ".items | add");
        fileSystem.SetStandardInput("""{"items":[1,2,3]}""");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-f", "/filter.jq")));
        await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal("6\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_FunctionsAndConditionals()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("""{"values":[3,1,2],"name":"  lokad  "}""");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation(
            "jq",
            """{min:(.values|min), max:(.values|max), trim:(.name|trim), ok:(if .values|length > 2 then true else false end)}""")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.True(exitCode == 0, fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal("{\n  \"min\": 1,\n  \"max\": 3,\n  \"trim\": \"lokad\",\n  \"ok\": true\n}\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_ArrayAndStringSlices()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("""{"values":[0,1,2,3],"name":"Lokad"}""");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", """{items:.values[1:3], text:.name[1:-1]}""")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("{\n  \"items\": [\n    1,\n    2\n  ],\n  \"text\": \"oka\"\n}\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_StringInterpolation()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("""{"name":"Example","n":2}""");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", """ "hello \(.name) \(.n + 1)" """)));
        await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal("\"hello Example 3\"\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_UnsupportedOptionReturnsError()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--bogus-flag", ".")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("unsupported option", fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_UnsupportedOptionAfterFilterReturnsError()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "--bogus-flag")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("unsupported option --bogus-flag", fileSystem.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_InvalidJsonInputReturnsParseError()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("""{"name":""");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(4, exitCode);
        Assert.Contains("jq:", fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(string.Empty, fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    // `1+2` computes since signs no longer join numbers; `1e` stays invalid.
    public async Task Jq_InvalidNumberLiteralReturnsFilterParseError()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1e")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Contains("jq:", fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(string.Empty, fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_InvalidUnicodeEscapeReturnsFilterParseError()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"\\uZZZZ\"")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Contains("jq:", fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(string.Empty, fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_InvalidJsonArgumentReturnsUsageError()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "$ARGS.positional[]", "--jsonargs", "not-json")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("jq:", fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(string.Empty, fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_ClosesFilterAndInputJqFileDescriptors()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile("/filter.jq", ".n");
        fileSystem.AddFile("/input.json", """{"n":1}""");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-f", "/filter.jq", "/input.json")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("1\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, fileSystem.OpenFileCount);
    }

    [Fact]
    public async Task Jq_ObjectConstructorPreservesValueStream()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{a:(1,2)}")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("{\n  \"a\": 1\n}\n{\n  \"a\": 2\n}\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_RangeWithoutArgumentsReturnsCompileError()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "range()")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Contains("jq:", fileSystem.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(string.Empty, fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_IndentAcceptsInlineLongOptionValue()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "--indent=4", "{a:1}")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("\n    \"a\"", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_UnbufferedIsAcceptedAsNoOp()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("""{"n":1}""");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--unbuffered", ".n")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("1\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_VersionPrintsLokadJq()
    {
        var fileSystem = new MockFileSystem();

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--version")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("Lokad jq\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
    }

    private static JqCommandInvocation BuildInvocation(string command, params string[] args)
    {
        return JqCommandInvocation.CreateWithStandardDescriptors(command, args, []);
    }
}


