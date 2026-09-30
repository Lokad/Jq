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
    public async Task Jq_IndentRejectsOutOfRange(string value)
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunCliAsync(host, "-n", "--indent", value, ".");
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
        Assert.Equal("[1,{\"a\":2}]\n", stdout);
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
        Assert.Equal("[1]\n", stdout);
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
}








