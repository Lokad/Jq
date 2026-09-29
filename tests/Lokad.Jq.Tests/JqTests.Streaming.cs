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
    public async Task Jq_StreamScalarAndContainers()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("1\n\"a\"\n[]\n{}\n");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[[],1]\n[[],\"a\"]\n[[],[]]\n[[],{}]\n", stdout);
    }

    [Fact]
    public async Task Jq_StreamNestedEvents()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[\"a\",[\"b\"]]");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[[0],\"a\"]\n[[1,0],\"b\"]\n[[1,0]]\n[[1]]\n", stdout);
    }

    [Fact]
    public async Task Jq_StreamObjectEvents()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"x\":1,\"y\":[]}");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[[\"x\"],1]\n[[\"y\"],[]]\n[[\"y\"]]\n", stdout);
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
        Assert.Equal("[[\"a\",0],1]\n[[\"a\",1],2]\n[[\"a\",1]]\n[[\"b\"],\"x\"]\n[[\"b\"]]\n", stdout);
    }

    [Fact]
    public async Task Jq_StreamTruncatedIsFatal()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1,2");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.Equal(4, exit);
        Assert.Equal("[[0],1]\n", stdout);
        Assert.Contains("jq: parse error: Unfinished JSON term at EOF", stderr);
    }

    [Fact]
    public async Task Jq_StreamErrorsRecover()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1, x, 2]");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream-errors", ".");
        Assert.True(exit == 0, stderr + "|" + stdout);
        Assert.Equal("[[0],1]\n[\"Invalid literal at line 1, column 6\",[1]]\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_StreamDeepNestingErrors()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(new string('[', 70));
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", ".");
        Assert.Equal(4, exit);
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
        Assert.Equal("\u001e[[0],1]\n\u001e[[0]]\n\u001e[[\"a\"],2]\n\u001e[[\"a\"]]\n", stdout);
    }

    [Fact]
    public async Task Jq_StreamSeqTruncationResyncs()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\u001e[1\n\u001e[2]\n\u001e");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "--stream", "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e[[0],2]\n\u001e[[0]]\n", stdout);
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

    [Fact]
    public async Task Jq_TostreamRoundtrip()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[0,[1,{\"a\":1},{\"b\":2}]]");
        var (exit, stdout, stderr) = await RunStreamAsync(host, ". as $dot | fromstream($dot | tostream) | . == $dot");
        Assert.True(exit == 0, stderr);
        Assert.Equal("true\n", stdout);
    }

    [Fact]
    public async Task Jq_TruncateStreamVectors()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("1");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "truncate_stream([[0],\"a\"],[[1,0],\"b\"],[[1,0]],[[1]])");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[[0],\"b\"]\n[[0]]\n", stdout);
        
    }

    [Fact]
    public async Task Jq_FromstreamTruncated()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("null");
        var (exit, stdout, stderr) = await RunStreamAsync(host, "fromstream(1 | truncate_stream([[0],\"a\"],[[1,0],\"b\"],[[1,0]],[[1]]))");
        Assert.True(exit == 0, stderr);
        Assert.Equal("[\"b\"]\n", stdout);
    }
}

