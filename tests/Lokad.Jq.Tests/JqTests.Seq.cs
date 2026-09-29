using System.Text;
using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    private static async Task<(int Exit, string Out, string Err)> RunSeqAsync(IJqHost host, params string[] args)
    {
        var tool = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", args, [])));
        var exit = await tool.ExecuteAsync(host, CancellationToken.None);
        var inner = host is MockFileSystem direct ? direct : ((ChunkedHost)host).Inner;
        return (exit, inner.GetOutput(JqFileDescriptor.StdOut), inner.GetOutput(JqFileDescriptor.StdErr));
    }

    private static string Seq(params string[] records) =>
        string.Concat(records.Select(record => "\u001e" + record));

    [Fact]
    public async Task Jq_SeqReadsRecordsAndFramesOutput()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("1\n", "2\n"));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e1\n\u001e2\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_SeqSkipsLeadingGarbage()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("garbage\u001e7\n\u001e");
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e7\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_SeqResyncsAfterBadRecord()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("1\n", "[\n", "2\n"));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e1\n\u001e2\n", stdout);
        Assert.Contains("jq: ignoring parse error: Truncated value (need RS to resync)", stderr);
    }

    [Fact]
    public async Task Jq_SeqRejectsBareTopNumber()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("12", ""));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("", stdout);
        Assert.Contains("Potentially truncated top-level numeric value (need RS to resync)", stderr);
    }

    [Fact]
    public async Task Jq_SeqBareNumberAtEofMentionsEof()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("12"));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("", stdout);
        Assert.Contains("Potentially truncated top-level numeric value at EOF", stderr);
    }

    [Fact]
    public async Task Jq_SeqCompleteLiteralAtEofEmits()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("true"));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001etrue\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_SeqSeparatorsSplitAcrossChunks()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput(Seq("{\"a\":1}\n", "[true]\n"));
        var (exit, stdout, stderr) = await RunSeqAsync(new ChunkedHost(inner, 2), "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e{\"a\":1}\n\u001e[true]\n", stdout);
    }

    [Fact]
    public async Task Jq_SeqEmptyInputWarnsAndEnds()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("");
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("", stdout);
        Assert.Contains("Unfinished abandoned text at EOF", stderr);
    }

    [Fact]
    public async Task Jq_SeqSlurpsRecords()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("1\n", "2\n"));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", "-s", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e[1,2]\n", stdout);
    }

    [Fact]
    public async Task Jq_SeqExplicitInputs()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("1\n", "2\n", "3\n"));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", "-n", "reduce inputs as $i (0; . + $i)");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e6\n", stdout);
    }
}

