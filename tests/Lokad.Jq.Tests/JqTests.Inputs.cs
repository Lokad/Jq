using System.Text;
using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    // Decorator over the in-memory host that caps every byte read, forcing
    // tokens, lines, and values to split across chunk boundaries.
    private sealed class ChunkedHost(MockFileSystem inner, int maxChunk) : IJqHost
    {
        public MockFileSystem Inner { get; } = inner;

        public async ValueTask<JqByteReadResult> ReadBytesAsync(
            JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            int capped = Math.Min(buffer.Length, maxChunk);
            return await Inner.ReadBytesAsync(descriptor, buffer[..capped], cancellationToken).ConfigureAwait(false);
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(
            JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            Inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
            Inner.OpenReadAsync(path, cancellationToken);

        public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) =>
            Inner.CloseDescriptorAsync(descriptor, cancellationToken);
    }

    private static async Task<(int Exit, string Out, string Err)> RunInputAsync(IJqHost host, params string[] args)
    {
        var tool = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", args, [])));
        var exit = await tool.ExecuteAsync(host, CancellationToken.None);
        var inner = host is ChunkedHost chunked ? chunked.Inner : (MockFileSystem)host;
        return (exit, inner.GetOutput(JqFileDescriptor.StdOut), inner.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Jq_InputChunkedBoundaries(int chunk)
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("{\"a\": [1, 2, {\"b\": \"x\\u00e9\"}], \"c\": null}\n[true,false]\n\"tail\"");
        var (exit, stdout, stderr) = await RunInputAsync(new ChunkedHost(inner, chunk), ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("{\n  \"a\": [\n    1,\n    2,\n    {\n      \"b\": \"xé\"\n    }\n  ],\n  \"c\": null\n}\n[\n  true,\n  false\n]\n\"tail\"\n", stdout);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task Jq_InputNonFiniteAcrossChunks(int chunk)
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("[nan, -Infinity, {\"a\": Infinity}]");
        var (exit, stdout, stderr) = await RunInputAsync(new ChunkedHost(inner, chunk), ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  null,\n  -1.7976931348623157E+308,\n  {\n    \"a\": 1.7976931348623157E+308\n  }\n]\n", stdout);
    }

    // Mid-container non-finite literals resume past their comma only when the
    // restored state expects a value; later positions leave the comma for the
    // post-value state, in arrays, objects, and nested mixes alike.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task Jq_InputNonFiniteMidContainerAcrossChunks(int chunk)
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("[1,nan,{\"a\":1,\"b\":-inf,\"c\":[2,infinity]}]");
        var (exit, stdout, stderr) = await RunInputAsync(new ChunkedHost(inner, chunk), ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  1,\n  null,\n  {\n    \"a\": 1,\n    \"b\": -1.7976931348623157E+308,\n    \"c\": [\n      2,\n      1.7976931348623157E+308\n    ]\n  }\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_InputLongScalarAcrossChunks()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("\"" + new string('y', 20000) + "\"");
        var (exit, stdout, stderr) = await RunInputAsync(new ChunkedHost(inner, 5), "length");
        Assert.True(exit == 0, stderr);
        Assert.Equal("20000\n", stdout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \t\r\n  ")]
    public async Task Jq_InputEmptyAndWhitespaceOnly(string stdin)
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput(stdin);
        var (exit, stdout, stderr) = await RunInputAsync(inner, ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("", stdout);
    }

    // Resumed non-finite literals keep cursor accounting: later documents
    // decode and line numbers stay exact across single- and multi-line resumes.
    [Theory]
    [InlineData("1\n[1,nan,2]\n3", ".", "1\n[\n  1,\n  null,\n  2\n]\n3\n")]
    [InlineData("1\n[1,nan,2]\n3", "input_line_number", "1\n2\n3\n")]
    [InlineData("{\n\"a\": 1,\n\"b\": nan\n}\n[5]", "input_line_number", "4\n5\n")]
    public async Task Jq_InputResumeKeepsPosition(string stdin, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(stdin);
        var (exit, stdout, stderr) = await RunInputAsync(host, filter);
        Assert.True(exit == 0, stderr);
        Assert.Equal(expected, stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_InputTruncatedAfterValidValues()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("1\n2\n[");
        var (exit, stdout, stderr) = await RunInputAsync(new ChunkedHost(inner, 2), ".");
        Assert.Equal(5, exit);
        Assert.Equal("1\n2\n", stdout);
        Assert.StartsWith("jq:", stderr);
    }

    [Fact]
    public async Task Jq_InputAbsurdExponentCannotOverflow()
    {
        // Absurd input exponents clamp through the double profile instead
        // of overflowing any parser buffer (upstream CVE-2023-50246).
        var host = new MockFileSystem();
        host.SetStandardInput("-10E-1000000001\n");
        var (exit, stdout, stderr) = await RunInputAsync(host, ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("-0\n", stdout);
    }

    [Fact]
    public async Task Jq_ControlBytesInInputAreMalformed()
    {
        // Raw control bytes inside strings fail like the reference issue
        // 2909 vectors; the diagnostic keeps the JSON reader wording per
        // the recorded input policy.
        foreach (byte control in new byte[] { 0x01, 0x1F })
        {
            var host = new MockFileSystem();
            host.SetStandardInputBytes(new byte[] { 0x22, control, 0x22 });
            var (exit, stdout, stderr) = await RunInputAsync(host, ".");
            Assert.Equal(5, exit);
            Assert.Empty(stdout);
            Assert.Contains("is invalid within a JSON string", stderr);
        }
    }

    [Fact]
    public async Task Jq_NulByteInInputIsNotTruncation()
    {
        // An embedded NUL byte fails the input instead of truncating it:
        // earlier values are kept and the exit stays 5 (upstream
        // CVE-2026-33948).
        var host = new MockFileSystem();
        host.SetStandardInput("{}\0{}");
        var (exit, stdout, stderr) = await RunInputAsync(host, ".");
        Assert.Equal(5, exit);
        Assert.Equal("{}\n", stdout);
        Assert.Contains("parse error", stderr);
    }

    [Fact]
    public async Task Jq_SlurpRejectsInvalidBytesAsMalformed()
    {
        // Slurp aggregation stages invalid bytes like the per-value paths:
        // JSON slurp reports the transcoding failure and raw slurp uses the
        // shared raw-segment decoder, both with empty output.
        var cases = new (string[] Args, byte[] Stdin)[]
        {
            (new string[] { "-s", "." }, new byte[] { 0x22, 0x61, 0xFF, 0x62, 0x22, 0x0A }),
            (new string[] { "-R", "-s", "." }, new byte[] { 0xFF }),
            (new string[] { "-R", "-s", "." }, new byte[] { 0xFF, 0x0A }),
        };
        foreach (var (args, stdin) in cases)
        {
            var host = new MockFileSystem();
            host.SetStandardInputBytes(stdin);
            var (exit, stdout, stderr) = await RunInputAsync(host, args);

            Assert.Equal(5, exit);
            Assert.Equal("", stdout);
            Assert.Contains("jq: parse error: Cannot transcode invalid UTF-8", stderr);
        }
    }

    [Fact]
    public async Task Jq_LeadingZeroInInputIsMalformed()
    {
        // A leading zero fails the whole input as malformed with no
        // partial output, like other reader rejections.
        var host = new MockFileSystem();
        host.SetStandardInput("[0,01]\n");
        var (exit, stdout, stderr) = await RunInputAsync(host, ".");

        Assert.Equal(5, exit);
        Assert.Equal("", stdout);
        Assert.Contains("leading zero", stderr);
    }

    [Fact]
    public async Task Jq_TrailingCommaInInputIsMalformed()
    {
        // Trailing commas are not valid JSON: the reader rejects them
        // like the reference instead of skipping to the bracket.
        var host = new MockFileSystem();
        host.SetStandardInput("[1,]\n");
        var (exit, stdout, stderr) = await RunInputAsync(host, ".");

        Assert.Equal(5, exit);
        Assert.Equal("", stdout);
        Assert.Contains("parse error", stderr);
    }

    [Fact]
    public async Task Jq_DuplicateInputKeysKeepFirstPosition()
    {
        // Like the reference object builder, later duplicates overwrite values
        // in place while the first-insertion position is kept.
        var first = new MockFileSystem();
        first.SetStandardInput("{\"a\":1,\"a\":2}");
        var (exit, stdout, stderr) = await RunInputAsync(first, ".");
        Assert.Equal(0, exit);
        Assert.Equal("{\n  \"a\": 2\n}\n", stdout);
        Assert.Empty(stderr);
        var ordered = new MockFileSystem();
        ordered.SetStandardInput("{\"b\":1,\"a\":2,\"b\":3}");
        var (exit2, stdout2, stderr2) = await RunInputAsync(ordered, ".");
        Assert.Equal(0, exit2);
        Assert.Equal("{\n  \"b\": 3,\n  \"a\": 2\n}\n", stdout2);
        Assert.Empty(stderr2);
    }

    [Fact]
    public async Task Jq_SlurpAddsAcrossValues()
    {
        // Slurp collects sibling values before folding, like the reference.
        var host = new MockFileSystem();
        host.SetStandardInput("[1,2][3,4]");
        var (exit, stdout, stderr) = await RunInputAsync(host, "-c", "-s", "add");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[1,2,3,4]\n", stdout);
    }

[Fact]
    public async Task Jq_InputHugeExponentClampsCleanly()
    {
        // Absurd exponents in input values clamp to the double profile like
        // literals do instead of failing: the reference pipes such values
        // through successfully (upstream #2367).
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\": 1E9999999999}");
        var (exit, stdout, stderr) = await RunInputAsync(host, ".a");
        Assert.True(exit == 0, stderr);
        Assert.Equal("1.7976931348623157E+308\n", stdout);
    }

[Fact]
    public async Task Jq_SlurpTrailingGarbageIsInputFailure()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("1\n2\n[");
        var (exit, stdout, stderr) = await RunInputAsync(inner, "-s", ".");
        Assert.Equal(5, exit);
        Assert.Empty(stdout);
        Assert.StartsWith("jq: parse error:", stderr);
    }

    [Fact]
    public async Task Jq_InputRawPreservesNullBytes()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("a\0b\nc\0d\ne");
        var (exit, stdout, stderr) = await RunInputAsync(inner, "-R", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"a\\u0000b\"\n\"c\\u0000d\"\n\"e\"\n", stdout);
    }

    [Fact]
    public async Task Jq_InputBomAppliesOncePerSource()
    {
        var cases = new (string Stdin, string ExpectedOut, int ExpectedExit)[]
        {
            ("\uFEFF1\n2\n", "1\n2\n", 0),
            ("1\n\uFEFF2\n", "1\n", 5),
            ("\uFEFF", "", 0),
            ("\uFEFF ", "", 0),
        };
        foreach (int chunk in new[] { 1, 2, 3 })
        {
            foreach (var (stdin, expectedOut, expectedExit) in cases)
            {
                var inner = new MockFileSystem();
                inner.SetStandardInput(stdin);
                var (exit, stdout, stderr) = await RunInputAsync(new ChunkedHost(inner, chunk), ".");
                Assert.True(exit == expectedExit, "chunk " + chunk + " stdin " + stdin.Length + ": " + stderr);
                Assert.Equal(expectedOut, stdout);
                if (expectedExit == 0) Assert.Empty(stderr);
            }
        }
    }

    [Fact]
    public async Task Jq_InputBomResetsPerFile()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/a.json", "\uFEFF1");
        inner.AddFile("/b.json", "\uFEFF2");
        var (exit, stdout, stderr) = await RunInputAsync(inner, ".", "/a.json", "/b.json");
        Assert.True(exit == 0, stderr);
        Assert.Equal("1\n2\n", stdout);
    }

    [Fact]
    public async Task Jq_InputSkipsLeadingBom()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("\uFEFF\"byte order mark\"");
        var (exit, stdout, stderr) = await RunInputAsync(inner, ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"byte order mark\"\n", stdout);
    }

    [Fact]
    public async Task Jq_InputRawPreservesCarriageReturns()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInputBytes("a\r\nb\rc\r"u8.ToArray());
        var (exit, stdout, stderr) = await RunInputAsync(inner, "-R", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"a\\r\"\n\"b\\rc\\r\"\n", stdout);
    }

    [Fact]
    public async Task Jq_InputRawLinesSplitAcrossChunks()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("ab\ncdefgh\n\nij");
        var (exit, stdout, stderr) = await RunInputAsync(new ChunkedHost(inner, 3), "-R", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"ab\"\n\"cdefgh\"\n\"\"\n\"ij\"\n", stdout);
    }

    [Fact]
    public async Task Jq_ExplicitReadsComposeWithRawLines()
    {
        // Explicit input and inputs read raw lines like implicit iteration
        // does, with per-line metadata following the reads.
        foreach (var (stdin, filter, expected) in new (string, string, string)[]
        {
            ("a\nb\nc\n", "[input, [inputs]]", "[\n  \"a\",\n  [\n    \"b\",\n    \"c\"\n  ]\n]\n"),
            ("a\nb\n", "[input | [., input_line_number]]", "[\n  [\n    \"a\",\n    1\n  ]\n]\n"),
            ("a\n", "[inputs]", "[\n  \"a\"\n]\n"),
        })
        {
            var inner = new MockFileSystem();
            inner.SetStandardInput(stdin);
            var (exit, stdout, stderr) = await RunInputAsync(inner, "-R", "-n", filter);
            Assert.True(exit == 0, stderr);
            Assert.Equal(expected, stdout);
            Assert.Empty(stderr);
        }
    }

    [Fact]
    public async Task Jq_InputConsecutiveFilesAndDash()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/a", "1\n");
        inner.AddFile("/b", "2\n");
        inner.SetStandardInput("0\n");
        var (exit, stdout, stderr) = await RunInputAsync(inner, ".", "/a", "-", "/b");
        Assert.True(exit == 0, stderr);
        Assert.Equal("1\n0\n2\n", stdout);
        Assert.Equal(0, inner.OpenFileCount);
    }

    [Fact]
    public async Task Jq_InputRepeatedStdinSharesPosition()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("1\n2\n");
        var (exit, stdout, stderr) = await RunInputAsync(inner, ".", "-", "-");
        Assert.True(exit == 0, stderr);
        Assert.Equal("1\n2\n", stdout);
        Assert.Empty(inner.ClosedDescriptors);
    }

    [Fact]
    public async Task Jq_InputExplicitInputsWithNullInput()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("1\n2\n3\n");
        var (exit, stdout, stderr) = await RunInputAsync(inner, "-n", "reduce inputs as $i (0; . + $i)");
        Assert.True(exit == 0, stderr);
        Assert.Equal("6\n", stdout);
    }

    [Fact]
    public async Task Jq_InputInterleavesWithOuterIteration()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("1\n2\n3\n4\n");
        var (exit, stdout, stderr) = await RunInputAsync(inner, "[., input]");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  1,\n  2\n]\n[\n  3,\n  4\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_InputAtEofIsCatchableBreak()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("1\n");
        var (exit, stdout, stderr) = await RunInputAsync(inner, "-n", "try input catch .");
        Assert.True(exit == 0, stderr);
        Assert.Equal("1\n", stdout);
        var empty = new MockFileSystem();
        empty.SetStandardInput("");
        var (exit2, stdout2, stderr2) = await RunInputAsync(empty, "-n", "try input catch .");
        Assert.True(exit2 == 0, stderr2);
        Assert.Equal("\"break\"\n", stdout2);
    }

    [Fact]
    public async Task Jq_MalformedInputAbortsRemainingInputs()
    {
        // Unlike sequence resync, a plain JSON parse failure is terminal:
        // earlier outputs are kept, later inputs never run, exit status is 5.
        var inner = new MockFileSystem();
        inner.SetStandardInput("1\n{bad}\n2\n");
        var (exit, stdout, stderr) = await RunInputAsync(inner, ".");
        Assert.Equal(5, exit);
        Assert.Equal("1\n", stdout);
        Assert.Contains("parse error", stderr);
    }

    [Fact]
    public async Task Jq_ExplicitReadFailuresTrackLivePosition()
    {
        // Explicit-read parse failures report the line being read like the
        // reference fgets-based count: the source is known from activation
        // even before the first value, and the failing line counts whether
        // or not it is terminated.
        foreach (var (stdin, args, exit, stdout, stderr) in new (string, string[], int, string, string)[]
        {
            ("{bad}", new string[] { "-n", "input" }, 5, "", "jq: error (at <stdin>:0): 'b' is an invalid start of a property name. Expected a '\"'. LineNumber: 0 | BytePositionInLine: 1.\n"),
            ("1\n{bad}\n", new string[] { "-n", "[inputs]" }, 5, "", "jq: error (at <stdin>:2): 'b' is an invalid start of a property name. Expected a '\"'. LineNumber: 0 | BytePositionInLine: 1.\n"),
            ("1\n{bad}", new string[] { "-n", "[inputs]" }, 5, "", "jq: error (at <stdin>:1): 'b' is an invalid start of a property name. Expected a '\"'. LineNumber: 0 | BytePositionInLine: 1.\n"),
            ("\n\n{bad}", new string[] { "-n", "input" }, 5, "", "jq: error (at <stdin>:2): 'b' is an invalid start of a property name. Expected a '\"'. LineNumber: 0 | BytePositionInLine: 1.\n"),
        })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(stdin);
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", args)));
            Assert.Equal(exit, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(stdout, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(stderr, host.GetOutput(JqFileDescriptor.StdErr));
        }
    }
    [Fact]
    public async Task Jq_InputMetadataFollowsExplicitReads()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("1\n2\n");
        var (exit, stdout, stderr) = await RunInputAsync(inner, "-n", "[input | [input_filename, input_line_number], inputs | [input_filename, input_line_number]]");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  [\n    \"<stdin>\",\n    1\n  ],\n  [\n    \"<stdin>\",\n    2\n  ]\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_InputBackpressureStopsAtFirstValue()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("1\n2\n3\n");
        var (exit, stdout, stderr) = await RunInputAsync(inner, "-n", "input | halt");
        Assert.True(exit == 0, stderr + "|" + stdout);
        Assert.True(inner.ReadBytesCallCount <= 2, "reads: " + inner.ReadBytesCallCount);
    }

    [Fact]
    public async Task Jq_InputEarlyTerminationClosesOwnedOnly()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/a", "1\n");
        inner.AddFile("/b", "2\n");
        inner.SetStandardInput("0\n");
        var (exit, _, stderr) = await RunInputAsync(inner, "., halt", "/a", "/b");
        Assert.True(exit == 0, stderr);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.DoesNotContain(inner.ClosedDescriptors, d => d == JqFileDescriptor.StdIn);
    }
}
