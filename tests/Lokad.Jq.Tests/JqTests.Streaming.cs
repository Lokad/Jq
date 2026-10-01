using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    private static async Task<(int Exit, string Out, string Err)> RunStreamAsync(IJqHost host, params string[] args)
    {
        var tool = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", args, [])));
        var exit = await tool.ExecuteAsync(host, CancellationToken.None);
        var inner = host is MockFileSystem direct ? direct : ((ChunkedHost)host).Inner;
        return (exit, inner.GetOutput(JqFileDescriptor.StdOut), inner.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_StreamEmitsResumedLeaves()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1,nan,2]");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  [\n    0\n  ],\n  1\n]\n[\n  [\n    1\n  ],\n  null\n]\n[\n  [\n    2\n  ],\n  2\n]\n[\n  [\n    2\n  ]\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_StreamScalarAndContainers()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("1\n\"a\"\n[]\n{}\n");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  [],\n  1\n]\n[\n  [],\n  \"a\"\n]\n[\n  [],\n  []\n]\n[\n  [],\n  {}\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_StreamRawInputWinsOverEventDecoding()
    {
        // Raw line decoding wins over streaming event decoding like the
        // reference; bare text stays string input instead of parse errors.
        var host = new MockFileSystem();
        host.SetStandardInput("a\nb\n");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "-R", "--stream", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\"a\"\n\"b\"\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_StreamNestedEvents()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"a\",[\"b\"]]");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  [\n    0\n  ],\n  \"a\"\n]\n[\n  [\n    1,\n    0\n  ],\n  \"b\"\n]\n[\n  [\n    1,\n    0\n  ]\n]\n[\n  [\n    1\n  ]\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_StreamObjectEvents()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"x\":1,\"y\":[]}");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  [\n    \"x\"\n  ],\n  1\n]\n[\n  [\n    \"y\"\n  ],\n  []\n]\n[\n  [\n    \"y\"\n  ]\n]\n", stdout);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Jq_StreamTokensSplitAcrossChunks(int chunk)
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("{\"a\": [1, 2], \"b\": \"x\"}");
        var (exit, stdout, stderr) = await RunStreamAsync(new ChunkedHost(inner, chunk), "--stream", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  [\n    \"a\",\n    0\n  ],\n  1\n]\n[\n  [\n    \"a\",\n    1\n  ],\n  2\n]\n[\n  [\n    \"a\",\n    1\n  ]\n]\n[\n  [\n    \"b\"\n  ],\n  \"x\"\n]\n[\n  [\n    \"b\"\n  ]\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_StreamSlurpComposesEvents()
    {
        // Slurped streaming input decodes element-wise: each top-level value
        // contributes its leaf and close events. The upstream shtest #3273
        // regression block expects exactly these four events for both the
        // bare and newline-terminated inputs, so this is byte parity.
        foreach (string stdin in new string[] { "[1][2]", "[1][2]\n" })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(stdin);
            var (exit, stdout, stderr) = await RunStreamAsync(host, "-c", "-s", "--stream", ".");
            Assert.True(exit == 0, stdin + ": " + stderr);
            Assert.Equal("[[[0],1],[[0]],[[0],2],[[0]]]\n", stdout);
        }
    }

[Fact]
    public async Task Jq_StreamTruncatedIsFatal()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1,2");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.Equal(5, exit);
        Assert.Equal("[\n  [\n    0\n  ],\n  1\n]\n", stdout);
        Assert.Contains("jq: parse error: Unfinished JSON term at EOF", stderr);
    }

    [Fact]
    public async Task Jq_StreamObjectErrorsReportUpstreamDiagnostics()
    {
        // Malformed objects fail with the reference streaming diagnostics,
        // keeping any events already produced like the shell suite expects.
        {
            var host = new MockFileSystem();
            host.SetStandardInput("{\"a\":1,\"b\",");
            var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
            Assert.Equal(5, exit);
            Assert.Equal("[\n  [\n    \"a\"\n  ],\n  1\n]\n", stdout);
            Assert.Contains("jq: parse error: Objects must consist of key:value pairs", stderr);
        }
        {
            var host = new MockFileSystem();
            host.SetStandardInput("{{\"a\":\"b\"}}");
            var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
            Assert.Equal(5, exit);
            Assert.Equal("", stdout);
            Assert.Contains("jq: parse error: Expected string key after '{', not '{'", stderr);
        }
        {
            var host = new MockFileSystem();
            host.SetStandardInput("{\"x\":\"y\",{\"a\":\"b\"}}");
            var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
            Assert.Equal(5, exit);
            Assert.Equal("[\n  [\n    \"x\"\n  ],\n  \"y\"\n]\n", stdout);
            Assert.Contains("jq: parse error: Expected string key after ',' in object, not '{'", stderr);
        }
        {
            var host = new MockFileSystem();
            host.SetStandardInput("{[\"a\",\"b\"]}");
            var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
            Assert.Equal(5, exit);
            Assert.Equal("", stdout);
            Assert.Contains("jq: parse error: Expected string key after '{', not '['", stderr);
        }
        {
            var host = new MockFileSystem();
            host.SetStandardInput("{\"x\":\"y\",[\"a\",\"b\"]}");
            var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
            Assert.Equal(5, exit);
            Assert.Equal("[\n  [\n    \"x\"\n  ],\n  \"y\"\n]\n", stdout);
            Assert.Contains("jq: parse error: Expected string key after ',' in object, not '['", stderr);
        }
    }

    [Fact]
    public async Task Jq_StreamErrorsRecover()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1, x, 2]");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream-errors", ".");
        Assert.True(exit == 0, stderr + "|" + stdout);
        Assert.Equal("[\n  [\n    0\n  ],\n  1\n]\n[\n  \"Invalid literal at line 1, column 6\",\n  [\n    1\n  ]\n]\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_StreamErrorsReportLivePathAtEof()
    {
        // Truncation at end of input reports the open-container path, not
        // an empty one: the reference shell suite pins [0] after `[`.
        var host = new MockFileSystem();
        host.SetStandardInput("[");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream-errors", "-c", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\"Unfinished JSON term at EOF at line 1, column 1\",[0]]\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_StreamDeepNestingErrors()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(new string('[', 70));
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.Equal(5, exit);
        Assert.Contains("nesting limit", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Jq_StreamSeqRecords()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\u001e[1]\n\u001e{\"a\":2}\n\u001e");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e[\n  [\n    0\n  ],\n  1\n]\n\u001e[\n  [\n    0\n  ]\n]\n\u001e[\n  [\n    \"a\"\n  ],\n  2\n]\n\u001e[\n  [\n    \"a\"\n  ]\n]\n", stdout);
    }

    [Fact]
    public async Task Jq_StreamSeqTruncationResyncs()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\u001e[1\n\u001e[2]\n\u001e");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e[\n  [\n    0\n  ],\n  2\n]\n\u001e[\n  [\n    0\n  ]\n]\n", stdout);
        Assert.Contains("jq: ignoring parse error: Truncated value", stderr);
    }

    [Fact]
    public async Task Jq_StreamCountsLargeShallowDocuments()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("[" + string.Join(",", Enumerable.Repeat("0", 5000)) + "]");
        var (exit, stdout, stderr) = await RunStreamAsync(new ChunkedHost(inner, 997), "-n", "--stream", "reduce inputs as $i (0; . + 1)");
        Assert.True(exit == 0, stderr);
        Assert.Equal("5001\n", stdout);
    }

    // Identity battery: empty containers, nesting, duplicate keys, and
    // empty-string keys all rebuild exactly through the event fold.
    [Theory]
    [InlineData("[0,[1,{\"a\":1},{\"b\":2}]]")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[[],{}]")]
    [InlineData("{\"a\":{},\"b\":[]}")]
    [InlineData("[[[[1]]]]")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"\":0}")]
    public async Task Jq_TostreamRoundtrip(string input)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var (exit, stdout, stderr) = await RunStreamAsync(host, ". as $dot | fromstream($dot | tostream) | . == $dot");
        Assert.True(exit == 0, stderr);
        Assert.Equal("true\n", stdout);
    }

    // Truncation depths over one event literal: depth 0 keeps every path,
    // depth 1 rebases longer paths, depth 2 drops paths of length 2 or less,
    // exactly per the builtin.jq conditional.
    [Theory]
    [InlineData("0", "[\n  [\n    [\n      0\n    ],\n    \"a\"\n  ],\n  [\n    [\n      1,\n      0\n    ],\n    \"b\"\n  ],\n  [\n    [\n      1,\n      0\n    ]\n  ],\n  [\n    [\n      1\n    ]\n  ]\n]\n")]
    [InlineData("1", "[\n  [\n    [\n      0\n    ],\n    \"b\"\n  ],\n  [\n    [\n      0\n    ]\n  ]\n]\n")]
    [InlineData("2", "[]\n")]
    public async Task Jq_TruncateStreamDepths(string input, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var (exit, stdout, stderr) = await RunStreamAsync(host, "[truncate_stream([[0],\"a\"],[[1,0],\"b\"],[[1,0]],[[1]])]");
        Assert.True(exit == 0, stderr);
        Assert.Equal(expected, stdout);
        Assert.Empty(stderr);
    }

    // Degenerate folds through the same definition: an empty stream emits
    // nothing, a root leaf rebuilds, and a lone close emits the null seed.
    [Theory]
    [InlineData("fromstream(empty)", "")]
    [InlineData("fromstream([[],[]])", "[]\n")]
    [InlineData("fromstream([[0]])", "null\n")]
    public async Task Jq_FromstreamDegenerateFolds(string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("null");
        var (exit, stdout, stderr) = await RunStreamAsync(host, filter);
        Assert.True(exit == 0, stderr);
        Assert.Equal(expected, stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_TruncateStreamVectors()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("1");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "truncate_stream([[0],\"a\"],[[1,0],\"b\"],[[1,0]],[[1]])");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  [\n    0\n  ],\n  \"b\"\n]\n[\n  [\n    0\n  ]\n]\n", stdout);
        
    }

    [Fact]
    public async Task Jq_FromstreamTruncated()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("null");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "fromstream(1 | truncate_stream([[0],\"a\"],[[1,0],\"b\"],[[1,0]],[[1]]))");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\n  \"b\"\n]\n", stdout);
    }
}


