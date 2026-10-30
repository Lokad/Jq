using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_BorrowedSourceCommentsAndWhitespaceNeedNoProportionalAdmission(bool comment)
    {
        string source = (comment ? "#" : "") + new string(' ', 256 * 1024) + "\n1";
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var counted = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", source], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, counted, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(root.PeakBytes, 1, 4096);
        Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 12);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_CompilerPreflightsRealLiteralAndIdentifierStorage(bool identifier)
    {
        string text = new('a', 128 * 1024);
        string source = identifier ? "def " + text + ": 1; 1" : "\"" + text + "\"";
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", source], []);
        Assert.Equal(5, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_CanceledLexingDoesNotAdmitBorrowedSource()
    {
        string source = "#" + new string(' ', 256 * 1024) + "\n1";
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var counted = new CountingMemoryBudget(root);
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, cancellation.Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Lexer.Tokenize(source, JqProgramSource.Inline, budget));
        Assert.Equal(0, counted.Counts.Reservations);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_CompilerAdmitsSmallNativeLiteralsInBatches()
    {
        string source = "module {items:[" + string.Join(',', Enumerable.Repeat("\"a\"", 512)) + "]}; 1";
        var root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        var counted = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", source], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, counted, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(counted.Counts.Reservations, 1, 8);
        Assert.Equal(0, counted.Counts.BytesReads);
        Assert.Equal(0, counted.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }
}
