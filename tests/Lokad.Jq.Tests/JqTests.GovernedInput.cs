using System.Text;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("try inputs catch \"caught\"")]
    [InlineData("inputs?")]
    public async Task Jq_GovernedInputRefusalPrecedesReadAndBypassesHandlers(string filter)
    {
        var memory = new TestMemoryBudget(8191);
        var host = new MockFileSystem();
        host.AddFile("/input", "1\n");
        var invocation = filter == "."
            ? BuildInvocation("jq", filter, "/input")
            : BuildInvocation("jq", "-n", filter, "/input");
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedInputGrowthRefusalPreservesPrefixAndStopsLaterFiles()
    {
        var memory = new TestMemoryBudget(40000);
        var host = new MockFileSystem();
        host.AddFile("/input", "a\n" + new string('b', 25000) + "\n");
        host.AddFile("/later", "later\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-R", "-r", ".", "/input", "/later")));

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("a\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(3, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.InRange(memory.PeakBytes, 32770, 40000);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedInputAndRawOutputKeepConstantCapacityAcrossRecords()
    {
        var memory = new TestMemoryBudget(20 * 1024);
        string input = string.Concat(Enumerable.Repeat("a\n", 10000));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-R", "-r", ".")));
        for (int repeat = 0; repeat < 2; repeat++)
        {
            var host = new MockFileSystem();
            host.SetStandardInput(input);
            Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Equal(Encoding.UTF8.GetBytes(input), host.GetOutputBytes(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Empty(host.ClosedDescriptors);
            Assert.Equal(0, memory.ReservedBytes);
        }
        Assert.InRange(memory.PeakBytes, 16386, 20 * 1024);
    }

    [Fact]
    public async Task Jq_GovernedInputKeepsPendingReadAndCleanupChargedAfterCancellation()
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem();
        inner.AddFile("/input", "1\n");
        var host = new MemoryReadBorrowHost(inner);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/input")));
        Task<int> execution = command.ExecuteAsync(host, memory, cancellation.Token);
        Memory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(8192, pending.Length);
        long charged = memory.ReservedBytes;
        Assert.True(charged >= 8192);
        cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, memory.ReservedBytes);
        pending.Span[0] = 42;
        host.Release.SetResult();
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, memory.ReservedBytes);
        host.CloseRelease.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_GovernedHostedReadResultOwnsCapacityAfterReadCompletion()
    {
        var memory = new TestMemoryBudget(16 * 1024);
        using var caller = memory.CreateScope();
        var budget = new JqBudget(JqUtf8RegexEngine.Instance, caller, CancellationToken.None);
        var host = new MockFileSystem();
        host.SetStandardInput("abc");
        host.BeforeByteRead = _ => Assert.Equal(12288, memory.ReservedBytes);

        using (var content = Assert.IsType<JqReadResult.Complete>(
            await host.TryReadAllBytesAsync(JqFileDescriptor.StdIn, budget, CancellationToken.None)))
        {
            Assert.Equal("abc"u8.ToArray(), content.Content.ToArray());
            Assert.Equal(4096, memory.ReservedBytes);
            Assert.Equal(4096, content.Memory.LastAdmittedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(12288, memory.PeakBytes);
        Assert.Empty(host.ClosedDescriptors);
    }

    [Theory]
    [InlineData("filter", 8191)]
    [InlineData("module", 16384)]
    [InlineData("rawfile", 8191)]
    [InlineData("slurpfile", 8191)]
    public async Task Jq_GovernedHostedReadRefusalClosesOwnedFileBeforeReading(string kind, long limit)
    {
        // Admit compilation first, then refuse the hosted read's backing storage.
        var memory = new TestMemoryBudget(limit);
        var host = new MockFileSystem();
        host.AddFile("/data", "[1]");
        host.AddFile("/program", ".");
        host.AddFile("/module.jq", "def f: 1;");
        string[] arguments = kind switch
        {
            "filter" => ["-n", "-f", "/program"],
            "module" => ["-n", "-L", "/", "import \"module\" as m {\"optional\":true}; m::f"],
            "rawfile" => ["-n", "--rawfile", "data", "/data", "$data"],
            _ => ["-n", "--slurpfile", "data", "/data", "$data"],
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedHostedReadGrowthRefusalRefundsUnpublishedStorage()
    {
        var memory = new TestMemoryBudget(16384);
        using var caller = memory.CreateScope();
        var budget = new JqBudget(JqUtf8RegexEngine.Instance, caller, CancellationToken.None);
        var host = new MockFileSystem();
        host.SetStandardInput(new string('x', 10000));

        await Assert.ThrowsAsync<JqQuotaException>(async () =>
            await host.TryReadAllBytesAsync(JqFileDescriptor.StdIn, budget, CancellationToken.None));
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Equal(12288, memory.PeakBytes);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Empty(host.ClosedDescriptors);
    }

    [Fact]
    public void Jq_GovernedStreamScannerRetainsAndRefundsTokenCapacity()
    {
        var memory = new TestMemoryBudget(2048);
        using var scope = memory.CreateScope();
        var budget = new JqBudget(JqUtf8RegexEngine.Instance, scope, CancellationToken.None);
        using (var scanner = new JqStreamScanner(budget, new JqRuntime(budget), false, false))
        {
            Assert.Null(scanner.Consume(Encoding.UTF8.GetBytes("\"" + new string('x', 300)), false).Event);
            Assert.Equal(512, memory.ReservedBytes);
            Assert.Equal(768, memory.PeakBytes);
            Assert.NotNull(scanner.Consume("\" "u8, false).Event);
            Assert.True(memory.ReservedBytes >= 512);
        }
        budget.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class MemoryReadBorrowHost(MockFileSystem inner) : IJqHost
    {
        internal TaskCompletionSource<Memory<byte>> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor,
            Memory<byte> buffer, CancellationToken cancellationToken)
        {
            Ready.TrySetResult(buffer);
            await Release.Task.ConfigureAwait(false);
            return await inner.ReadBytesAsync(descriptor, buffer, cancellationToken).ConfigureAwait(false);
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor,
            ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(path, cancellationToken);

        public async Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
        {
            Closing.TrySetResult();
            await CloseRelease.Task.ConfigureAwait(false);
            return await inner.CloseDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
    }
}
