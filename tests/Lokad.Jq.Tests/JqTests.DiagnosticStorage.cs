using System.Text;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("debug", "[\"DEBUG:\",\"ready\"]\n")]
    [InlineData("debug(.)", "[\"DEBUG:\",\"ready\"]\n")]
    [InlineData("stderr", "ready")]
    public async Task Jq_DiagnosticEffectPrecedesInputAndOutput(string effect, string expected)
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("42\n");
        var host = new DiagnosticHost(inner) { AwaitDiagnosticBeforeRead = true };
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "(\"ready\" | " + effect + " | empty), input")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal(expected, inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal("42\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal([JqFileDescriptor.StdErr, JqFileDescriptor.StdOut], host.Writes);
        Assert.Equal(1, inner.ReadBytesCallCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_SequenceWarningPrecedesTheNextHostRead()
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("\u001ebad\n\u001e");
        var host = new DiagnosticHost(inner) { AwaitDiagnosticBeforeRead = true, FirstReadNotFinal = true };
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--seq", ".")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.StartsWith("jq: ignoring parse error:", inner.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(2, inner.ReadBytesCallCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("try (range(10000) | debug | empty) catch \"caught\"", 0)]
    [InlineData("(range(10000) | debug | empty)?", 7)]
    [InlineData("try (range(10000) | debug(.) | empty) catch \"caught\"", 7)]
    [InlineData("(range(10000) | \"x\" | stderr | empty)?", 0)]
    public async Task Jq_DiagnosticClosureStopsBeforeFurtherEvaluation(string expression, int status)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var counted = new CountingMemoryBudget(memory);
        var inner = new MockFileSystem();
        inner.AddFile("/first", "42\n");
        inner.AddFile("/later", "invalid JSON");
        var host = new DiagnosticHost(inner)
        {
            StderrResult = status == 0 ? JqAppendResult.Closed : JqAppendResult.Failure(status),
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"before\", (" + expression + "), \"after\"", "/first", "/later")));

        Assert.Equal(status, await command.ExecuteAsync(host, counted, CancellationToken.None));
        Assert.Equal("before\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal([JqFileDescriptor.StdOut, JqFileDescriptor.StdErr], host.Writes);
        Assert.Equal(1, inner.ReadBytesCallCount);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, counted.Counts.Refusals);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 7)]
    [InlineData(true, 7)]
    public async Task Jq_DiagnosticClosureDoesNotHideOwnedCloseFaults(bool overflow, int status)
    {
        Exception sentinel = overflow ? new OverflowException("close sentinel") : new ArgumentException("close sentinel");
        var inner = new MockFileSystem();
        inner.AddFile("/input", "42\n");
        var host = new DiagnosticHost(inner)
        {
            StderrResult = status == 0 ? JqAppendResult.Closed : JqAppendResult.Failure(status),
            CloseFailure = sentinel,
        };
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "try debug catch \"caught\"", "/input")));

        var failure = await Assert.ThrowsAsync<JqHostFailureException>(() => command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Same(sentinel, failure.InnerException);
        Assert.Equal(1, host.CloseCalls);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",42]\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("debug", "[\"DEBUG:\",\"é\"]\n")]
    [InlineData("stderr", "é")]
    [InlineData("stderr", "")]
    public async Task Jq_LongDiagnosticStreamUsesBoundedStorage(string effect, string message)
    {
        const int count = 10000;
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "range(" + count + ") | " + (message.Length == 0 ? "\"\"" : "\"é\"") + " | " + effect + " | empty")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(message, count))), host.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.Equal(count, host.AppendCallCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_DiagnosticAdmissionCancellationDoesNotWrite()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        var host = new MockFileSystem();
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token))
            { Stderr = (host, JqFileDescriptor.StdErr) })
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await context.EmitStderrAsync(new string('a', 10000)));
            Assert.Equal(0, host.AppendCallCount);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_EmptyDiagnosticAvoidsAdmissionAndChecksLifetimeAndCancellation()
    {
        var root = new BenchmarkMemoryBudget(0);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        using var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token))
            { Stderr = (host, JqFileDescriptor.StdErr) };
        await context.EmitStderrAsync("");
        await context.EmitStderrAsync(ReadOnlyMemory<byte>.Empty);
        Assert.Equal(2, host.AppendCallCount);
        Assert.Equal(0, root.ReservedBytes);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await context.EmitStderrAsync(""));
        context.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await context.EmitStderrAsync(ReadOnlyMemory<byte>.Empty));
        Assert.Equal(2, host.AppendCallCount);
    }

    private sealed class DiagnosticHost(MockFileSystem inner) : IJqHost
    {
        internal bool AwaitDiagnosticBeforeRead { get; init; }
        internal bool FirstReadNotFinal { get; init; }
        internal JqAppendResult? StderrResult { get; init; }
        internal Exception? CloseFailure { get; init; }
        internal int CloseCalls { get; private set; }
        internal List<JqFileDescriptor> Writes { get; } = [];
        private readonly TaskCompletionSource _diagnostic = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (AwaitDiagnosticBeforeRead && (!FirstReadNotFinal || inner.ReadBytesCallCount != 0))
                await _diagnostic.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            JqByteReadResult read = await inner.ReadBytesAsync(descriptor, buffer, cancellationToken).ConfigureAwait(false);
            return FirstReadNotFinal && inner.ReadBytesCallCount == 1
                ? JqByteReadResult.Success(read.BytesRead, false) : read;
        }

        public async Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Writes.Add(descriptor);
            if (descriptor == JqFileDescriptor.StdErr) _diagnostic.TrySetResult();
            JqAppendResult result = await inner.AppendWhileOpenAsync(descriptor, content, cancellationToken).ConfigureAwait(false);
            return descriptor == JqFileDescriptor.StdErr && StderrResult is { } stderr ? stderr : result;
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);
        public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
        {
            CloseCalls++;
            if (CloseFailure is { } failure) throw failure;
            return inner.CloseDescriptorAsync(descriptor, cancellationToken);
        }
    }
}
