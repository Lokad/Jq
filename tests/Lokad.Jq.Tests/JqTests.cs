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

    [Fact]
    public async Task Jq_RejectsHugeRepeatUnderTry()
    {
        // Limit failures stage instead of surfacing as values, so try/catch cannot observe them.
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("\"abc\"\n");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "try (. * 1000000000) catch .")));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var exitCode = await tool.ExecuteAsync(fileSystem, cancellation.Token);

        Assert.Equal(5, exitCode);
        Assert.Equal("jq: string result exceeds the 16 MiB UTF-16 limit\n", fileSystem.GetOutput(JqFileDescriptor.StdErr));
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
    [InlineData("\"\" * 1000000000", "\"\"\n")]
    // Input form of the same upstream repeat vector; the literal form above covers the same path.
    [InlineData("\"\" | . * 1000000000", "\"\"\n")]
    [InlineData("\"ab\" + \"cd\"", "\"abcd\"\n")]
    [InlineData("[2 * 3, 2 + 3, 2.5 * 4, -3 * 2]", "[\n  6,\n  5,\n  10,\n  -6\n]\n")]
    [InlineData("[\"a\", \"ab\", \"abc\"] | [.[] * 3]", "[\n  \"aaa\",\n  \"ababab\",\n  \"abcabcabc\"\n]\n")]
    [InlineData("[-1.0, -0.5, 0.0, 0.5, 1.0, 1.5, 3.7, 10.0] | [.[] * \"abc\"]", "[\n  null,\n  null,\n  \"\",\n  \"\",\n  \"abc\",\n  \"abc\",\n  \"abcabcabc\",\n  \"abcabcabcabcabcabcabcabcabcabc\"\n]\n")]
    [InlineData("\"abc\" | [. * (nan,-nan)]", "[\n  null,\n  null\n]\n")]
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
    public async Task Jq_ArgsFormsPopulateVariables()
    {
        foreach (var (flags, expected) in new (string[], string)[]
        {
            (["-n", "--arg", "foo", "1", "--argjson", "bar", "2", "{$foo, $bar} | ., . == $ARGS.named"], "{\n  \"foo\": \"1\",\n  \"bar\": 2\n}\ntrue\n"),
            (["-n", "--args", "$ARGS.positional", "foo", "bar", "baz"], "[\n  \"foo\",\n  \"bar\",\n  \"baz\"\n]\n"),
            (["-n", "--jsonargs", "$ARGS.positional", "null", "true", "[]", "{}"], "[\n  null,\n  true,\n  [],\n  {}\n]\n"),
            (["-n", "$ARGS.positional", "--args", "foo", "1", "--jsonargs", "2", "{}", "--args", "3", "4"], "[\n  \"foo\",\n  \"1\",\n  2,\n  {},\n  \"3\",\n  \"4\"\n]\n"),
            (["-n", "$ARGS.positional", "--args", "--jsonargs"], "[]\n"),
            (["--args", "-rn", "--", "$ARGS.positional[0]", "bar"], "bar\n"),
            (["--args", "-rn", "1", "--", "$ARGS.positional[0]", "bar"], "1\n"),
        })
        {
            var fileSystem = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", flags)));
            var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);
            Assert.Equal(0, exitCode);
            Assert.Equal(expected, fileSystem.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
        }

        foreach (string[] flags in new string[][]
        {
            new string[] { "-n", "--jsonargs", "null", "invalid" },
            new string[] { "-n", "--jsonargs", "null", "--", "invalid" },
        })
        {
            var fileSystem = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", flags)));
            var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);
            Assert.Equal(2, exitCode);
            Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdOut));
            Assert.Contains("jq:", fileSystem.GetOutput(JqFileDescriptor.StdErr));
        }
    }


    [Fact]
    public async Task Jq_SlurpKeepsResumedValues()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("1\n[1,nan,2]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-s", ".")));
        Assert.Equal(0, await tool.ExecuteAsync(fileSystem, CancellationToken.None));
        Assert.Equal("[\n  1,\n  [\n    1,\n    null,\n    2\n  ]\n]\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
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
    public async Task Jq_RawNulLinesComposeWithSlurpAndInputs()
    {
        // Like the reference shell suite, NUL-containing raw lines compare
        // whole under -Rse and stream element-wise through inputs under -Rne.
        foreach (string[] flags in new string[][]
        {
            new string[] { "-R", "-s", "-e", ". == \"a\\u0000b\\nc\\u0000d\\ne\"" },
            new string[] { "-R", "-n", "-e", "[inputs] == [\"a\\u0000b\", \"c\\u0000d\", \"e\"]" },
        })
        {
            var fileSystem = new MockFileSystem();
            fileSystem.SetStandardInput("a\0b\nc\0d\ne");
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", flags)));
            Assert.Equal(0, await tool.ExecuteAsync(fileSystem, CancellationToken.None));
            Assert.Equal("true\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
        }
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
    public async Task Jq_FractionalSliceEndRoundsUp()
    {
        // Reference start-down/end-up rules: the start truncates while a
        // fractional end within bounds rounds up, for arrays and strings.
        var arrays = new MockFileSystem();
        arrays.SetStandardInput("[0,1,2,3,4,5,6,7,8,9]");
        var arrayTool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".[1.5:3.5]")));
        Assert.Equal(0, await arrayTool.ExecuteAsync(arrays, CancellationToken.None));
        Assert.Equal("[\n  1,\n  2,\n  3\n]\n", arrays.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(arrays.GetOutput(JqFileDescriptor.StdErr));

        var strings = new MockFileSystem();
        strings.SetStandardInput("\"abcdef\"");
        var stringTool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".[1.5:3.5]")));
        Assert.Equal(0, await stringTool.ExecuteAsync(strings, CancellationToken.None));
        Assert.Equal("\"bcd\"\n", strings.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(strings.GetOutput(JqFileDescriptor.StdErr));
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

        Assert.Equal(5, exitCode);
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
    public async Task Jq_AsciiOutputEscapesNonAscii()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("\"é\"");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-a", ".")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("\"\\u00E9\"\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
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
    public async Task Jq_ControlCharactersRenderEscaped()
    {
        // Like the reference, DEL renders as an escape (with the encoder's
        // uppercase hex case) rather than raw.
        var fileSystem = new MockFileSystem();
        fileSystem.SetStandardInput("\" \u007f\"");

        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".")));
        var exitCode = await tool.ExecuteAsync(fileSystem, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("\" \\u007F\"\n", fileSystem.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(fileSystem.GetOutput(JqFileDescriptor.StdErr));
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


