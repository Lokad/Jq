using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    private static async Task<(int Exit, string Out, string Err)> RunCliAsync(MockFileSystem host, params string[] args)
    {
        var tool = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", args, [])));
        var exit = await tool.ExecuteAsync(host, CancellationToken.None);
        return (exit, host.GetOutput(JqFileDescriptor.StdOut), host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_RepeatedBoolFlagsAreIdempotent()
    {
        foreach (var (flags, stdin, expected) in new (string[], string, string)[]
        {
            (["-n", "-n", "."], "", "null\n"),
            (["-R", "-R", "."], "0", "\"0\"\n"),
            (["-s", "-s", "-n", "."], "", "null\n"),
            (["-c", "-c", "."], "0", "0\n"),
        })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(stdin);
            var (exit, stdout, stderr) = await RunCliAsync(host, flags);
            Assert.True(exit == 0, string.Join(" ", flags) + "|" + stderr);
            Assert.Equal(expected, stdout);
        }

        var version = new MockFileSystem();
        var (versionExit, versionOut, versionErr) = await RunCliAsync(version, "-V", "-V");
        Assert.Equal(0, versionExit);
        Assert.Equal("Lokad jq\n", versionOut);
        Assert.Equal("", versionErr);
    }

    [Theory]
    [InlineData("-5", "-5\n")]
    [InlineData("-1.5", "-1.5\n")]
    public async Task Jq_NegativeNumbersAreFiltersNotOptions(string filter, string expected)
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", filter);
        Assert.True(exit == 0, stderr);
        Assert.Equal(expected, stdout);
    }

    [Fact]
    public async Task Jq_DoubleDashEndsOptionParsing()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("0");
        var (exit, stdout, stderr) = await RunCliAsync(host, "--", "-5");
        Assert.True(exit == 0, stderr);
        Assert.Equal("-5\n", stdout);
    }

    [Fact]
    public async Task Jq_DoubleDashMakesFlagsPositional()
    {
        var host = new MockFileSystem();
        host.AddFile("/data", "7");
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--", "-n");
        Assert.Equal(3, exit);
        Assert.Contains("unsupported function n", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Jq_DashFileReadsStdin()
    {
        var host = new MockFileSystem();
        host.AddFile("/a", "1\n");
        host.SetStandardInput("0\n");
        var (exit, stdout, stderr) = await RunCliAsync(host, ".", "/a", "-", "/a");
        Assert.True(exit == 0, stderr);
        Assert.Equal("1\n0\n1\n", stdout);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_FirstFromFileWins()
    {
        var host = new MockFileSystem();
        host.AddFile("/p1", ".a");
        host.AddFile("/p2", "{\"a\":5}");
        var (exit, stdout, stderr) = await RunCliAsync(host, "-f", "/p1", "-f", "/p2");
        Assert.True(exit == 0, stderr);
        Assert.Equal("5\n", stdout);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_FirstArgWins()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--arg", "a", "1", "--arg", "a", "2", "$a");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"1\"\n", stdout);
    }

    [Fact]
    public async Task Jq_FirstArgjsonWins()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--argjson", "a", "1", "--argjson", "a", "2", "$a");
        Assert.True(exit == 0, stderr);
        Assert.Equal("1\n", stdout);
    }

    [Fact]
    public async Task Jq_IndentSevenUsesSevenSpaces()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--indent", "7", "[1]");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[" + "\n" + "       1" + "\n" + "]" + "\n", stdout);
    }

    [Theory]
    [InlineData("--indent", "-1")]
    [InlineData("--indent=-1", null)]
    public async Task Jq_IndentMinusOneSelectsTabs(string flag, string? value)
    {
        var host = new MockFileSystem();
        string[] args = value is null ? [flag, "-n", "[1]"] : [flag, value, "-n", "[1]"];
        var (exit, stdout, stderr) = await RunCliAsync(host, args);
        Assert.True(exit == 0, stderr);
        Assert.Equal("[" + "\n" + "\t1" + "\n" + "]" + "\n", stdout);
    }

    [Theory]
    [InlineData("8")]
    [InlineData("abc")]
    [InlineData(" 4")]
    [InlineData("4x")]
    [InlineData("9999999999")]
    [InlineData("-9999999999")]
    [InlineData("+8")]
    public async Task Jq_IndentRejectsOutOfRange(string value)
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--indent", value, ".");
        Assert.Equal(2, exit);
        Assert.Contains("--indent takes a number between -1 and 7", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Jq_IndentCombinedOverflowIsRangeError()
    {
        // The combined spelling must report the reference diagnostic too,
        // not a binder conversion error.
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--indent=9999999999", ".");
        Assert.Equal(2, exit);
        Assert.Contains("--indent takes a number between -1 and 7", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Jq_RawfileBindsFileText()
    {
        var host = new MockFileSystem();
        host.AddFile("/note.txt", "hello\n");
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--rawfile", "note", "/note.txt", "$note");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"hello\\n\"\n", stdout);
    }

    [Fact]
    public async Task Jq_SlurpfileBindsJsonArray()
    {
        var host = new MockFileSystem();
        host.AddFile("/data.json", "1\n{\"a\":2}\n");
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--slurpfile", "data", "/data.json", "$data");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  1,\n  {\n    \"a\": 2\n  }\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_SlurpfileRejectsBadJson()
    {
        var host = new MockFileSystem();
        host.AddFile("/data.json", "1\nnope\n");
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--slurpfile", "data", "/data.json", "$data");
        Assert.Equal(2, exit);
        Assert.Contains("Bad JSON in --slurpfile data /data.json", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Jq_RawfileMissingFileIsSystemError()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--rawfile", "x", "/nope", "$x");
        Assert.Equal(2, exit);
        Assert.Contains("Bad JSON in --rawfile x /nope", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Jq_FileVariablesJoinArgsNamed()
    {
        var host = new MockFileSystem();
        host.AddFile("/data.json", "1\n");
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--slurpfile", "data", "/data.json", "$ARGS.named.data");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  1\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_BinaryFlagIsAcceptedNoOp()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("0");
        var (exit, stdout, stderr) = await RunCliAsync(host, "-b", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("0\n", stdout);
    }


    [Fact]
    public async Task Jq_MissingInputFileIsSystemError()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, ".", "/missing");
        Assert.Equal(2, exit);
        Assert.Contains("cannot open", stderr);
        Assert.Empty(stdout);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_OptionsStillParseAfterArgsMarkers()
    {
        // Flags and valued options keep working after --args, matching the reference:
        // the first non-option operand stays the filter and later ones turn positional.
        var compact = new MockFileSystem();
        compact.SetStandardInput("null");
        var (compactExit, compactOut, compactErr) = await RunCliAsync(compact, "--tab", "{a:1}", "--args", "-c");
        Assert.Equal(0, compactExit);
        Assert.Equal("{\"a\":1}\n", compactOut);
        Assert.Empty(compactErr);

        var named = new MockFileSystem();
        var (namedExit, namedOut, namedErr) = await RunCliAsync(named, "--args", "-n", "$ARGS.positional", "a", "b");
        Assert.Equal(0, namedExit);
        Assert.Equal("[\n  \"a\",\n  \"b\"\n]\n", namedOut);
        Assert.Empty(namedErr);
    }

    [Fact]
    public async Task Jq_FirstNonOptionStaysFilterInsideArgsMarkers()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--args", "a", "b");
        Assert.Equal(3, exit);
        Assert.Empty(stdout);
    }

    [Fact]
    public async Task Jq_FilesBeforeMarkersStayInputFiles()
    {
        var host = new MockFileSystem();
        host.AddFile("/data.json", "{\"f\":1}");
        var (exit, stdout, stderr) = await RunCliAsync(host, "[$ARGS.positional]", "/data.json", "--args", "a");
        Assert.Equal(0, exit);
        Assert.Equal("[\n  [\n    \"a\"\n  ]\n]\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_SeparatorPreservesArgsMarkers()
    {
        // -- only stops option parsing like the reference; an active --args mode
        // keeps diverting later operands into positional arguments.
        var filter = new MockFileSystem();
        var (filterExit, filterOut, filterErr) = await RunCliAsync(filter, "-n", "--args", "--", "x");
        Assert.Equal(3, filterExit);
        Assert.Empty(filterOut);

        var preserved = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(preserved, "-n", "$ARGS.positional", "--args", "a", "--", "b");
        Assert.Equal(0, exit);
        Assert.Equal("[\n  \"a\",\n  \"b\"\n]\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_RawInputFlagSurvivesArgsMarkers()
    {
        // -R after --args stays an option and the first non-option operand stays
        // the filter; a distinct filter proves the fallback dot is not used.
        var host = new MockFileSystem();
        host.SetStandardInput("hi");
        var (exit, stdout, stderr) = await RunCliAsync(host, "--args", "-R", "$ARGS.positional", "x");
        Assert.Equal(0, exit);
        Assert.Equal("[\n  \"x\"\n]\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_LastArgsMarkerSelectsJsonParsing()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--args", "a", "--jsonargs", "b", "$ARGS.positional");
        Assert.Equal(2, exit);
        Assert.Contains("invalid JSON", stderr);
        Assert.Empty(stdout);
    }

    [Fact]
    public async Task Jq_ArgsMarkersKeepPerOperandParsing()
    {
        // Each post-marker operand uses the marker active when it was seen, like
        // the reference: earlier string operands stay strings after --jsonargs.
        var mixed = new MockFileSystem();
        var (mixedExit, mixedOut, mixedErr) = await RunCliAsync(mixed, "-n", "$ARGS.positional", "--args", "a", "--jsonargs", "{\"b\":1}");
        Assert.Equal(0, mixedExit);
        Assert.Equal("[\n  \"a\",\n  {\n    \"b\": 1\n  }\n]\n", mixedOut);
        Assert.Empty(mixedErr);

        var back = new MockFileSystem();
        var (backExit, backOut, backErr) = await RunCliAsync(back, "-n", "$ARGS.positional", "--jsonargs", "{\"b\":1}", "--args", "c");
        Assert.Equal(0, backExit);
        Assert.Equal("[\n  {\n    \"b\": 1\n  },\n  \"c\"\n]\n", backOut);
        Assert.Empty(backErr);
    }
}
