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
