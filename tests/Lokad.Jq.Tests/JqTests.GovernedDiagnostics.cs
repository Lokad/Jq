using System.Text;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_GovernedEmptyStderrStillAwaitsHostAndPreservesCancellation(bool cancel)
    {
        var memory = new TestMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem();
        var host = new MemoryBorrowHost(inner) { DelayedDescriptor = JqFileDescriptor.StdErr };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "(\"\" | stderr), \"later\"")));
        Task<int> execution = command.ExecuteAsync(host, memory, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(pending.IsEmpty);
        Assert.Equal("\n", inner.GetOutput(JqFileDescriptor.StdOut));
        long retained = memory.ReservedBytes;
        Assert.True(retained > 0);
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(retained, memory.ReservedBytes);
        host.Release.SetResult();
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.Equal("\n", inner.GetOutput(JqFileDescriptor.StdOut));
        }
        else
        {
            Assert.Equal(0, await execution);
            Assert.Equal("\nlater\n", inner.GetOutput(JqFileDescriptor.StdOut));
        }
        Assert.Empty(inner.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedEmptyDiagnosticQueueKeepsPriorBytesAndValidatesLifetime()
    {
        var root = new TestMemoryBudget(8);
        var memory = new CountingMemoryBudget(root);
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), JqProgramSource.Inline,
            new JqBudget(JqExecutionPolicy.Default, memory, CancellationToken.None));
        context.EmitStderr("é");
        MemoryCallCounts admitted = memory.Counts;
        context.EmitStderr("");
        context.EmitStderr(ReadOnlyMemory<byte>.Empty, false);
        Assert.Equal(admitted, memory.Counts);
        Assert.Equal(2, root.ReservedBytes);
        var chunks = context.TakePendingStderr();
        Assert.Equal(3, chunks.Count);
        Assert.Equal("é"u8.ToArray(), chunks[0].ToArray());
        Assert.True(chunks[1].IsEmpty);
        Assert.True(chunks[2].IsEmpty);
        Assert.Equal(2, root.ReservedBytes);
        context.ReleaseDrainedStderr();
        Assert.Equal(0, root.ReservedBytes);
        MemoryCallCounts refunded = memory.Counts;
        context.EmitStderr("");
        context.ReleaseDrainedStderr();
        Assert.Equal(refunded, memory.Counts);
        context.Dispose();
        Assert.Throws<ObjectDisposedException>(() => context.EmitStderr(""));
        Assert.Throws<ObjectDisposedException>(() => context.EmitStderr(ReadOnlyMemory<byte>.Empty, false));
        Assert.Throws<ObjectDisposedException>(() => context.ReleaseDrainedStderr());
    }

    [Fact]
    public void Jq_GovernedEmptyDiagnosticsStillCheckCancellationAndPermitRefunds()
    {
        var memory = new TestMemoryBudget(8);
        using var cancellation = new CancellationTokenSource();
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), JqProgramSource.Inline,
            new JqBudget(JqExecutionPolicy.Default, memory, cancellation.Token));
        context.EmitStderr("x");
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => context.EmitStderr(""));
        Assert.Throws<OperationCanceledException>(() => context.EmitStderr(ReadOnlyMemory<byte>.Empty, false));
        Assert.Equal(1, memory.ReservedBytes);
        context.TakePendingStderr();
        context.ReleaseDrainedStderr();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_GovernedDiagnosticKeepsExactUtf8ChargeUntilHostCompletes(bool cancel)
    {
        var memory = new TestMemoryBudget(1024);
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem();
        var host = new MemoryBorrowHost(inner) { DelayedDescriptor = JqFileDescriptor.StdErr };
        byte[] expected = Encoding.UTF8.GetBytes("jq: café 😀\n");
        Task<bool> writing = JqDiagnosticWriter.TryWriteAsync(host, JqFileDescriptor.StdErr,
            ["jq: ", "café ", "😀"], true, memory, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, pending.ToArray());
        Assert.Equal(expected.Length, memory.ReservedBytes);
        if (cancel) cancellation.Cancel();
        Assert.False(writing.IsCompleted);
        Assert.Equal(expected.Length, memory.ReservedBytes);
        host.Release.SetResult();
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
            Assert.Empty(inner.GetOutputBytes(JqFileDescriptor.StdErr));
        }
        else
        {
            Assert.True(await writing);
            Assert.Equal(expected, inner.GetOutputBytes(JqFileDescriptor.StdErr));
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(expected.Length, memory.PeakBytes);
    }

    [Fact]
    public async Task Jq_GovernedDiagnosticRefusalLeavesExistingOwnerIntact()
    {
        var memory = new TestMemoryBudget(8);
        Assert.True(memory.TryReserve(8, out IMemoryReservation? retained));
        using (retained)
        {
            var host = new MockFileSystem();
            Assert.False(await JqDiagnosticWriter.TryWriteAsync(host, JqFileDescriptor.StdErr,
                ["large diagnostic"], true, memory, CancellationToken.None));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(8, retained.Bytes);
            Assert.Equal(8, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedErrorPayloadRefusalPreservesPrefixAndStopsLaterFiles()
    {
        const long limit = 1024 * 1024;
        var memory = new TestMemoryBudget(limit);
        var host = new MockFileSystem();
        string payload = new('x', 20000);
        host.AddFile("/input", "\"" + payload + "\"");
        host.AddFile("/later", "\"later\"");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", error", "/input", "/later")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null)
                Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 4096, out pressure));
        };

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedHaltPayloadRefusalReturnsResourceStatusInsteadOfHaltCode()
    {
        var memory = new TestMemoryBudget(16 * 1024);
        var host = new MockFileSystem();
        string payload = new('x', 20000);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r",
            "--arg", "payload", payload, "\"prefix\", ($payload | halt_error(7))")));

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("error", 5, "jq: error (at /input:1): café 😀\n")]
    [InlineData("halt_error(7)", 7, "café 😀")]
    public async Task Jq_GovernedTerminalPayloadKeepsUtf8StorageUntilStderrCompletes(
        string operation, int status, string expected)
    {
        var memory = new TestMemoryBudget(128 * 1024);
        var inner = new MockFileSystem();
        inner.AddFile("/input", "\"café 😀\"");
        var host = new MemoryBorrowHost(inner) { DelayedDescriptor = JqFileDescriptor.StdErr };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", operation, "/input")));
        Task<int> execution = command.ExecuteAsync(host, memory, CancellationToken.None);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), pending.ToArray());
        Assert.True(memory.ReservedBytes >= pending.Length + 2 * "café 😀".Length);
        Assert.False(execution.IsCompleted);
        host.Release.SetResult();
        Assert.Equal(status, await execution);
        Assert.Equal(expected, inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
