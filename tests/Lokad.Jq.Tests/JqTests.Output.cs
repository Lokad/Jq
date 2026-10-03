using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    private static async Task<(int Exit, string Out, string Err)> RunOutputAsync(MockFileSystem host, params string[] args)
    {
        var tool = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", args, [])));
        var exit = await tool.ExecuteAsync(host, CancellationToken.None);
        return (exit, host.GetOutput(JqFileDescriptor.StdOut), host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_SortKeysOrdersObjects()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"b\":1,\"a\":{\"d\":4,\"c\":3}}");
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-S", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("{\n  \"a\": {\n    \"c\": 3,\n    \"d\": 4\n  },\n  \"b\": 1\n}\n", stdout);
    }

    [Fact]
    public async Task Jq_SortKeysRendersCoalescedDuplicates()
    {
        // Duplicate keys coalesce last-wins at parse, so sorted output
        // orders the surviving entries without re-splitting them.
        var host = new MockFileSystem();
        host.SetStandardInput("{\"b\":1,\"a\":2,\"b\":3}");
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-S", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("{\n  \"a\": 2,\n  \"b\": 3\n}\n", stdout);
    }

    [Fact]
    public async Task Jq_SortKeysLeavesArraysAndScalars()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[{\"b\":1,\"a\":2},1,\"x\"]");
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-S", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  {\n    \"a\": 2,\n    \"b\": 1\n  },\n  1,\n  \"x\"\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_SortKeysLeavesTojsonUnsorted()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"b\":1,\"a\":2}");
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-S", "tojson");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"{\\\"b\\\":1,\\\"a\\\":2}\"\n", stdout);
    }

    [Fact]
    public async Task Jq_RawOutput0TerminatesWithNul()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"a\",\"b\"]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", ["--raw-output0", ".[]"], [])));
        var exit = await tool.ExecuteAsync(host, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal("a\0b\0"u8.ToArray(), host.GetOutputBytes(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_RawOutput0FramesAllValues()
    {
        // Like raw output, strings render without quotes; every value,
        // scalar or structured, ends with a NUL terminator instead of LF.
        {
            var host = new MockFileSystem();
            host.SetStandardInput("[1,\"a\",null,true]");
            var tool = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", ["--raw-output0", ".[]"], [])));
            var exit = await tool.ExecuteAsync(host, CancellationToken.None);
            Assert.Equal(0, exit);
            Assert.Equal("1\0a\0null\0true\0"u8.ToArray(), host.GetOutputBytes(JqFileDescriptor.StdOut));
        }
        {
            var host = new MockFileSystem();
            host.SetStandardInput("{\"a\":1}");
            var (exit, stdout, stderr) = await RunOutputAsync(host, "--raw-output0", ".");
            Assert.Equal(0, exit);
            Assert.Equal("{\n  \"a\": 1\n}\0"u8.ToArray(), host.GetOutputBytes(JqFileDescriptor.StdOut));
            Assert.Equal("", stderr);
        }
    }

    [Fact]
    public async Task Jq_JoinConcatenatesRawOutputs()
    {
        foreach (var (stdin, filter, expected) in new (string, string, string)[]
        {
            ("[1,2]", ".[]", "12"),
            ("[\"a\",\"b\"]", ".[]", "ab"),
        })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(stdin);
            var (exit, stdout, stderr) = await RunOutputAsync(host, "-j", filter);
            Assert.True(exit == 0, stderr);
            Assert.Equal(expected, stdout);
        }
    }

    [Fact]
    public async Task Jq_RawOutput0RejectsNulStrings()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"a\\u0000b\"]");
        var (exit, stdout, stderr) = await RunOutputAsync(host, "--raw-output0", ".[]");
        Assert.Equal(5, exit);
        Assert.Equal("", stdout);
        Assert.Contains("Cannot dump a string containing NUL", stderr);
    }

    [Fact]
    public async Task Jq_RawOutput0KeepsPrefixBeforeNulFailure()
    {
        // Values rendered before a NUL-containing string stay on stdout when
        // --raw-output0 fails with exit 5 (upstream pins a\0 as the prefix).
        var host = new MockFileSystem();
        host.SetStandardInput("[\"a\", \"c\\u0000d\", \"b\"]");
        var (exit, _, stderr) = await RunOutputAsync(host, "--raw-output0", ".[]");
        Assert.Equal(5, exit);
        Assert.Equal("a\0"u8.ToArray(), host.GetOutputBytes(JqFileDescriptor.StdOut));
        Assert.Contains("Cannot dump a string containing NUL", stderr);
    }

    [Fact]

    public async Task Jq_JoinImpliesRawOutput()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-n", "-j", "\"a\"");
        Assert.True(exit == 0, stderr);
        Assert.Equal("a", stdout);
    }

    [Fact]
    public async Task Jq_JoinOutputDuplicatesOnStderr()
    {
        // With join-output, human-readable rendering goes to stdout while
        // diagnostics still mirror the same bytes on stderr.
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-n", "-c", "-j", "\"hello\\nworld\", null, [false, 0], {\"foo\":[\"bar\"]}, \"\\n\" | stderr");
        Assert.True(exit == 0, stderr);
        Assert.Equal("hello\nworldnull[false,0]{\"foo\":[\"bar\"]}\n", stdout);
        Assert.Equal("hello\nworldnull[false,0]{\"foo\":[\"bar\"]}\n", stderr);
    }

    [Fact]
    public async Task Jq_ColorOutputNeedsTerminalHost()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-C", "-n", ".");
        Assert.Equal(2, exit);
        Assert.Contains("terminal-capable host", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Jq_MonochromeIsAcceptedNoOp()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":1}");
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-M", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("{\n  \"a\": 1\n}\n", stdout);
    }

    [Theory]
    [InlineData("true", 0, "true\n")]
    [InlineData("1", 0, "1\n")]
    [InlineData("\"x\"", 0, "\"x\"\n")]
    [InlineData("{}", 0, "{}\n")]
    [InlineData("false", 1, "false\n")]
    [InlineData("null", 1, "null\n")]
    public async Task Jq_ExitStatusCategories(string filter, int expectedExit, string expectedOut)
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-n", "-e", filter);
        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedOut, stdout);
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task Jq_ExitStatusNoOutputIsFour()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-n", "-e", "empty");
        Assert.Equal(4, exit);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task Jq_ExitStatusAcrossInputs()
    {
        // Like the reference shell suite, selection with no matches exits 4
        // while a final false value exits 1 across multiple inputs.
        var selected = new MockFileSystem();
        selected.SetStandardInput("{\"i\": 1}\n{\"i\": 2}\n{\"i\": 3}\n");
        var (exit4, stdout4, stderr4) = await RunOutputAsync(selected, "--exit-status", "select(.i==4)");
        Assert.Equal(4, exit4);
        Assert.Equal("", stdout4);
        Assert.Equal("", stderr4);
        var falsy = new MockFileSystem();
        falsy.SetStandardInput("{\"i\": 1}\n{\"i\": 2}\n{\"i\": 3}\n");
        var (exit1, stdout1, stderr1) = await RunOutputAsync(falsy, "--exit-status", "select(.i==2) | false");
        Assert.Equal(1, exit1);
        Assert.Equal("false\n", stdout1);
        Assert.Equal("", stderr1);
    }

    [Fact]
    public async Task Jq_RuntimeErrorContinuesWithNextInput()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("1\n\"a\"\n3\n");
        var (exit, stdout, stderr) = await RunOutputAsync(host, ". + 1");
        Assert.Equal(5, exit);
        Assert.Equal("2\n4\n", stdout);
        Assert.Contains("jq:", stderr);
    }


    [Fact]
    public async Task Jq_HelpPrintsWithoutReads()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("0");
        var (exit, stdout, stderr) = await RunOutputAsync(host, "--help", ".", "/missing");
        Assert.Equal(0, exit);
        Assert.Contains("Usage: jq [options] filter [files...]", stdout);
        Assert.Contains("--sort-keys", stdout);
        Assert.Contains("--exit-status", stdout);
        Assert.Contains("--binary", stdout);
        Assert.Contains("--color-output", stdout);
        Assert.Contains("first non-option", stdout);
        Assert.Equal("", stderr);
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(new[] { "-hV" }, true)]
    [InlineData(new[] { "-Vh" }, false)]
    [InlineData(new[] { "-h", "-V" }, true)]
    [InlineData(new[] { "-V", "-h" }, false)]
    [InlineData(new[] { "--help", "--version" }, true)]
    [InlineData(new[] { "--version", "--help" }, false)]
    public async Task Jq_HelpVersionFirstSeenWins(string[] flags, bool expectHelp)
    {
        // Like the reference shell suite, whichever of help/version comes
        // first wins: -hV prints help while -Vh prints the version.
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunOutputAsync(host, flags);
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        if (expectHelp)
            Assert.Contains("Usage: jq [options] filter [files...]", stdout);
        else
            Assert.Equal("Lokad jq\n", stdout);
    }

    [Fact]
    public async Task Jq_HelpIgnoresPostSeparatorOperands()
    {
        // Options after -- stay positional: --help still wins and a later
        // version flag never flips the first-seen decision.
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunOutputAsync(host, "--help", "--", "-V");
        Assert.Equal(0, exit);
        Assert.Contains("Usage: jq [options] filter [files...]", stdout);
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task Jq_VersionAndConfigPrintWithoutReads()
    {
        var version = new MockFileSystem();
        var (exitV, outV, errV) = await RunOutputAsync(version, "--version", ".", "/missing");
        Assert.Equal(0, exitV);
        Assert.Equal("Lokad jq\n", outV);
        Assert.Equal("", errV);
        Assert.Equal(0, version.ReadBytesCallCount);

        var config = new MockFileSystem();
        var (exitC, outC, errC) = await RunOutputAsync(config, "--build-configuration");
        Assert.Equal(0, exitC);
        Assert.Contains("Lokad jq", outC);
        Assert.Contains("PCRE.NET", outC);
        Assert.Equal("", errC);
        Assert.Equal(0, config.ReadBytesCallCount);
    }

    [Theory]
    [InlineData("--run-tests")]
    [InlineData("--debug-dump-disasm")]
    [InlineData("--debug-trace")]
    [InlineData("--debug-trace=all")]
    public async Task Jq_ToolOnlySwitchesAreRejectedExplicitly(string flag)
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunOutputAsync(host, flag, "-n", ".");
        Assert.Equal(2, exit);
        Assert.Contains("Unknown option", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Jq_PrettyOutputUsesLfBytes()
    {
        var host = new MockFileSystem();
        var (exit, stdout, stderr) = await RunOutputAsync(host, "-n", "--indent", "2", "{\"b\":[1,2]}");
        Assert.True(exit == 0, stderr);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(stdout);
        Assert.DoesNotContain((byte)13, bytes);
        Assert.Equal("{\n  \"b\": [\n    1,\n    2\n  ]\n}\n", stdout);
    }
}

