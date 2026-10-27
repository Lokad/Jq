using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[", "1]\n", "[1]")]
    [InlineData("\"a", "b\"\n", "\"ab\"")]
    [InlineData("1", "2\n", "12")]
    [InlineData("tr", "ue\n", "true")]
    [InlineData("Na", "N\n", "null")]
    public async Task Jq_LogicalJsonContinuesAcrossOwnedSources(string first, string second, string expected)
    {
        foreach (int chunk in new[] { 1, 8192 })
        foreach (string mode in new[] { "outer", "explicit", "slurp" })
        {
            var inner = new MockFileSystem();
            inner.AddFile("/first", first);
            inner.AddFile("/second", second);
            string[] arguments = mode switch
            {
                "explicit" => ["-nc", "inputs", "/first", "/second"],
                "slurp" => ["-sc", ".", "/first", "/second"],
                _ => ["-c", ".", "/first", "/second"],
            };
            var memory = new BenchmarkMemoryBudget(64 * 1024);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
            Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(inner, chunk), Utf8RegexAdapter.Instance, memory, CancellationToken.None));
            Assert.Equal((mode == "slurp" ? "[" + expected + "]" : expected) + "\n", inner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(2, inner.ClosedDescriptors.Count);
            Assert.Equal(0, inner.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("json", "[1]\n")]
    [InlineData("stream", "[[0],1]\n[[0]]\n")]
    [InlineData("stream-errors", "[[0],1]\n[[0]]\n")]
    [InlineData("sequence", "\u001e[1]\n")]
    [InlineData("stream sequence", "\u001e[[0],1]\n\u001e[[0]]\n")]
    public async Task Jq_LogicalJsonHeaderSpansEmptySourcesAndBomBytes(string mode, string expected)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/empty", "");
        inner.AddFile("/first", new byte[] { 0xEF });
        inner.AddFile("/second", new byte[] { 0xBB });
        byte[] tail = Encoding.UTF8.GetBytes((mode.Contains("sequence", StringComparison.Ordinal) ? "\u001e" : "") + "[1]\n");
        inner.AddFile("/third", new byte[] { 0xBF }.Concat(tail).ToArray());
        string[] options = mode switch
        {
            "stream" => ["--stream"],
            "stream-errors" => ["--stream-errors"],
            "sequence" => ["--seq"],
            "stream sequence" => ["--stream", "--seq"],
            _ => [],
        };
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", [.. options, "-c", ".", "/empty", "/first", "/second", "/third"])));
        Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(inner, 1), Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(4, inner.ClosedDescriptors.Count);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Jq_LogicalJsonPartialBomAtEofIsEmpty(int bytes)
    {
        var inner = new MockFileSystem();
        inner.SetStandardInputBytes(new byte[] { 0xEF, 0xBB }[..bytes]);
        var result = await RunInputAsync(new ChunkedHost(inner, 1), "-c", ".");
        Assert.Equal((0, "", ""), result);
    }

    [Theory]
    [InlineData("\"é🚀\"", 4)]
    [InlineData("[1,2]", 1)]
    public async Task Jq_LogicalJsonPreservesUtf8AndSourceMetadata(string value, int split)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var inner = new MockFileSystem();
        inner.AddFile("/first", bytes[..split]);
        inner.AddFile("/second", bytes[split..]);
        var result = await RunInputAsync(new ChunkedHost(inner, 1), "-c", "[input_filename,input_line_number,.]", "/first", "/second");
        Assert.Equal((0, "[\"/second\",1," + (value[0] == '"' ? "\"é\\ud83d\\ude80\"" : value) + "]\n", ""), result);
        Assert.Equal(2, inner.ClosedDescriptors.Count);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"done\"")]
    [InlineData("42\n")]
    public async Task Jq_LogicalJsonCompleteValueDoesNotRequestAnotherSourceOrRead(string value)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first", value);
        var host = new PendingSuffixHost(inner, Encoding.UTF8.GetByteCount(value));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "first(inputs)", "/first", "/missing")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.False(host.PendingRead.Task.IsCompleted);
        Assert.Equal(value.TrimEnd('\n') + "\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_LogicalJsonPendingNextSourceRetainsBorrowThroughCancellation(bool cancel)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first", "[1,");
        inner.AddFile("/second", "2]\n");
        var host = new InputProgressHost(inner, 1);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "try input catch .", "/first", "/second")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token);
        await host.Pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(1, inner.OpenFileCount);
        long pending = memory.ReservedBytes;
        Assert.True(pending > 0);
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(pending, memory.ReservedBytes);
        host.Release.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else Assert.Equal(0, await execution);
        Assert.Equal(cancel ? "" : "[1,2]\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, inner.ClosedDescriptors.Count);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_LogicalJsonOpenMissDoesNotSplitAPendingNumber()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first", "1");
        inner.AddFile("/last", "2\n");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "try inputs catch \"caught\"", "/first", "/missing", "/last")));
        Assert.Equal(2, await command.ExecuteAsync(inner, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("12\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: cannot open /missing\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, inner.ClosedDescriptors.Count);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_LogicalJsonRefusalAfterSourceTransitionRemainsTerminal()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first", "[");
        inner.AddFile("/second", "\"" + new string('x', 256 * 1024) + "\"]\n");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "try input catch \"caught\"", "/first", "/second")));
        Assert.Equal(5, await command.ExecuteAsync(inner, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, inner.ClosedDescriptors.Count);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_LogicalJsonLongStreamKeepsBoundedAdmissionAcrossFiles()
    {
        string text = string.Concat(Enumerable.Repeat("[1,2]\n", 32768));
        int split = text.Length / 2 + 3;
        var inner = new MockFileSystem();
        inner.AddFile("/first", text[..split]);
        inner.AddFile("/second", text[split..]);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "empty", "/first", "/second")));
        Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(inner, 257), Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, inner.ClosedDescriptors.Count);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
