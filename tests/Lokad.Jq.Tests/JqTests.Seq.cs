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
    public async Task Jq_SeqRawInputWinsOverSequenceFraming()
    {
        // Raw line decoding wins over sequence records like the reference
        // (raw installs no JSON parser, so SEQ/STREAMING flags stay inert);
        // outputs still carry sequence framing.
        var host = new MockFileSystem();
        host.SetStandardInput("a\nb\n");
        var (exit, stdout, stderr) = await RunSeqAsync(host, "-R", "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e\"a\"\n\u001e\"b\"\n", stdout);
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
        Assert.Equal("\u001e{\n  \"a\": 1\n}\n\u001e[\n  true\n]\n", stdout);
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
    public async Task Jq_SeqUnframedTailIsAbandoned()
    {
        // Like the reference waiting state, content that never sees a
        // record separator is abandoned text even when it parses as
        // complete values: warnings in auto-drain, errors via inputs.
        var bare = new MockFileSystem();
        bare.SetStandardInput("1\n");
        var (bareExit, bareOut, bareErr) = await RunSeqAsync(bare, "-c", "--seq", ".");
        Assert.Equal(0, bareExit);
        Assert.Equal("", bareOut);
        Assert.Contains("Unfinished abandoned text at EOF", bareErr);
        var sting = new MockFileSystem();
        sting.SetStandardInput("\"foo");
        var (stingExit, stingOut, stingErr) = await RunSeqAsync(sting, "-c", "--seq", ".");
        Assert.Equal(0, stingExit);
        Assert.Equal("", stingOut);
        Assert.Contains("Unfinished abandoned text at EOF at line 1, column 4", stingErr);
        var pulled = new MockFileSystem();
        pulled.SetStandardInput("1\n");
        var (pulledExit, pulledOut, pulledErr) = await RunSeqAsync(pulled, "-c", "-e", "-n", "--seq", "[inputs] == []");
        Assert.Equal(5, pulledExit);
        Assert.Equal("", pulledOut);
        Assert.Contains("Unfinished abandoned text at EOF at line 2, column 0", pulledErr);
    }

    [Fact]
    public async Task Jq_SeqTruncatedRecordExitsFour()
    {
        // Like the reference, a truncated final record warns and exits 4
        // under --exit-status.
        var host = new MockFileSystem();
        host.SetStandardInput("\"foo");
        var (exit, stdout, stderr) = await RunSeqAsync(host, "-c", "-e", "--seq", ".");
        Assert.Equal(4, exit);
        Assert.Equal("", stdout);
        Assert.Contains("ignoring parse error", stderr);
    }

    [Fact]
    public async Task Jq_SeqSlurpsRecords()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("1\n", "2\n"));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", "-s", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e[\n  1,\n  2\n]\n", stdout);
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

    [Fact]
    public async Task Jq_SeqFramesEachOutput()
    {
        // Sequence framing prefixes every output value, including values
        // fanned out from a single input, like the reference.
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("[1,2]\n"));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".[]");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e1\n\u001e2\n", stdout);
        Assert.Empty(stderr);
    }
}

