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
        Assert.Contains("jq: ignoring parse error: Truncated value at line 3, column 1", stderr);
    }

    [Fact]
    public async Task Jq_SeqRejectsBareTopNumber()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(Seq("12", ""));
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("", stdout);
        Assert.Contains("Potentially truncated top-level numeric value at line 1, column 4", stderr);
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
    public async Task Jq_SeqSkipsBarePrefixAcrossSources()
    {
        // Pre-separator content is skipped per source like the reference
        // continuous parser: the bare first file contributes no record and
        // no warning once a later file frames records.
        var host = new MockFileSystem();
        host.AddFile("/a.seq", "1\n");
        host.AddFile("/b.seq", "\u001e2\n");
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".", "/a.seq", "/b.seq");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e2\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Jq_SeqStreamAbandonedWarns()
    {
        // The streaming sequence path reports unframed tails the same way:
        // warnings in auto-drain with no records.
        var host = new MockFileSystem();
        host.SetStandardInput("1\n");
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--stream", "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("", stdout);
        Assert.Contains("Unfinished abandoned text at EOF at line 2, column 0", stderr);
    }

    [Fact]
    public async Task Jq_SeqTrailingNumberAfterRecordsWarns()
    {
        // Once a separator has been seen, a trailing bare number takes the
        // truncation path rather than the abandoned one.
        var host = new MockFileSystem();
        host.SetStandardInput("\u001e1\n2");
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e1\n", stdout);
        Assert.Contains("Potentially truncated top-level numeric value at EOF", stderr);
    }

    [Fact]
    public async Task Jq_SeqSlurpAbandonsUnframedTail()
    {
        // Slurp aggregation over an unframed tail warns and yields the empty
        // framed array instead of failing or recording values.
        var host = new MockFileSystem();
        host.SetStandardInput("1\n");
        var (exit, stdout, stderr) = await RunSeqAsync(host, "-s", "--seq", ".");
        Assert.True(exit == 0, stderr);
        Assert.Equal("\u001e[]\n", stdout);
        Assert.Contains("Unfinished abandoned text at EOF", stderr);
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
    public async Task Jq_SeqInvalidBytesResyncWithWarning()
    {
        // Invalid bytes resync like other malformed records: warn,
        // skip, and keep a zero status when nothing else fails.
        var host = new MockFileSystem();
        host.SetStandardInputBytes(new byte[] { 0x1E, 0xFF, 0x0A });
        var (exit, stdout, stderr) = await RunSeqAsync(host, "--seq", ".");

        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
        Assert.Contains("jq: ignoring parse error", stderr);
    }

    [Fact]
    public async Task Jq_SeqResyncWarningCounts()
    {
        // Upstream shell vectors expect three positioned resync warnings over
        // these RS records; truncation reports the record-end line/column
        // like the reference. This pins the byte-exact stderr with stdout
        // and status.
        var host = new MockFileSystem();
        host.SetStandardInput("1\u001e2 3\n[0,1\u001e[4,5]true\"ab\"{\"c\":4\u001e{}{\"d\":5,\"e\":6\"\u001efalse\n");
        var (exit, stdout, stderr) = await RunSeqAsync(host, "-c", "-e", "-s", "--seq", ". == [2,3,[4,5],true,\"ab\",{},false]");
        Assert.Equal(1, exit);
        Assert.Equal("\u001efalse\n", stdout);
        Assert.Equal("jq: ignoring parse error: Truncated value at line 2, column 5\n" + "jq: ignoring parse error: Truncated value at line 2, column 25\n" + "jq: ignoring parse error: Truncated value at line 2, column 41\n", stderr);
    }

    [Fact]
    public async Task Jq_SeqMalformedWarningsCarryPositions()
    {
        // Malformed records report the offending byte like the reference:
        // reader wording with the global line/column before the resync note.
        var first = new MockFileSystem();
        first.SetStandardInput("\u001e{bad}\u001e");
        var (firstExit, firstOut, firstErr) = await RunSeqAsync(first, "--seq", ".");
        Assert.True(firstExit == 0, firstErr);
        Assert.Equal("", firstOut);
        Assert.Equal("jq: ignoring parse error: 'b' is an invalid start of a property name. Expected a '\"'. at line 1, column 3 (need RS to resync)\n", firstErr);

        var second = new MockFileSystem();
        second.SetStandardInput("\u001e:5\u001e");
        var (secondExit, secondOut, secondErr) = await RunSeqAsync(second, "--seq", ".");
        Assert.True(secondExit == 0, secondErr);
        Assert.Equal("", secondOut);
        Assert.Equal("jq: ignoring parse error: ':' is an invalid start of a value. at line 1, column 2 (need RS to resync)\n", secondErr);
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

