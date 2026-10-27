using Lokad.Jq.Benchmarking;
using System.Text;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_GovernedRawOutputPreflightsReplacementPeakAndPreservesBorrowedOwner()
    {
        const long limit = 64 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        using IMemoryScope caller = memory.CreateScope();
        Assert.True(caller.TryReserve(2, out IMemoryReservation? retained));
        using (retained)
        {
            var host = new MockFileSystem();
            IMemoryReservation? pressure = null;
            host.BeforeByteAppend = () =>
            {
                if (pressure is null)
                    Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 3, out pressure));
            };
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", "\"xx\",\"longxx\"")));

            Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, caller, CancellationToken.None));
            Assert.Equal("xx\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.NotNull(pressure);
            Assert.Equal(2 + pressure.Bytes, memory.ReservedBytes);
            pressure.Dispose();
            Assert.Equal(2, memory.ReservedBytes);
            Assert.Equal(2, retained.Bytes);
            Assert.True(caller.TryReserve(limit - 2, out IMemoryReservation? available));
            available.Dispose();
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedRawOutputReusesAllowanceAcrossRecordsAndInvocations()
    {
        var memory = new BenchmarkMemoryBudget(16 * 1024);
        long? observed = null;
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", "range(100) | \"xx\"")));
        for (int run = 0; run < 3; run++)
        {
            var host = new MockFileSystem();
            host.BeforeByteAppend = () =>
            {
                observed ??= memory.ReservedBytes;
                Assert.Equal(observed.Value, memory.ReservedBytes);
            };
            Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
            Assert.Equal(string.Concat(Enumerable.Repeat("xx\n", 100)), host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, memory.ReservedBytes);
        }
        // Compiler workspace can peak before the steady output owner takes over.
        Assert.InRange(memory.PeakBytes - observed.GetValueOrDefault(), 0, 4096);
    }

    [Fact]
    public void Jq_GovernedJsonBufferAccountsForCapacityAndBorrowedGrowthStorage()
    {
        var memory = new BenchmarkMemoryBudget(1024);
        using var scope = memory.CreateScope();
        var budget = new JqBudget(Utf8RegexAdapter.Instance, scope, CancellationToken.None);
        using (var buffer = new JqJsonBuffer(budget))
        {
            "abc"u8.CopyTo(buffer.GetSpan(3));
            buffer.Advance(3);
            ReadOnlyMemory<byte> old = buffer.WrittenMemory;
            Assert.Equal(256, memory.ReservedBytes);
            buffer.GetMemory(300);
            Assert.Equal("abc"u8.ToArray(), old.ToArray());
            Assert.Equal(812, memory.ReservedBytes);
            buffer.ReleaseReplacedStorage();
            Assert.Equal(556, memory.ReservedBytes);
            buffer.Reset();
            Assert.Equal(556, memory.ReservedBytes);
            buffer.GetMemory(100);
            Assert.Equal(556, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(812, memory.PeakBytes);
    }

    [Fact]
    public void Jq_GovernedBufferRefusalLeavesStorageAndReservationUnchanged()
    {
        var memory = new BenchmarkMemoryBudget(8);
        byte[] buffer = [];
        using (var reservation = new JqMemoryReservation(memory))
        {
            reservation.ReplaceBuffer(ref buffer, 3, CancellationToken.None);
            buffer[0] = 42;
            byte[] previous = buffer;
            Assert.Throws<JqQuotaException>(() => reservation.ReplaceBuffer(ref buffer, 6, CancellationToken.None));
            Assert.Same(previous, buffer);
            Assert.Equal(42, buffer[0]);
            Assert.Equal(3, reservation.LastAdmittedBytes);
            Assert.Equal(3, memory.ReservedBytes);
            Assert.Throws<ArgumentOutOfRangeException>(() => reservation.Resize(-1));
            reservation.Resize(0);
            Assert.Equal(0, memory.ReservedBytes);
            reservation.Dispose();
            Assert.Throws<ObjectDisposedException>(() => reservation.Resize(1));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Jq_GovernedOutputKeepsChargesThroughCancellationAndCleanup(bool raw, bool cancel)
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem();
        inner.AddFile("/input", "\"xx\"");
        var host = new MemoryBorrowHost(inner) { DelayCleanup = true };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", raw ? "-r" : "-c", ".", "/input")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        long charged = memory.ReservedBytes;
        Assert.True(charged >= pending.Length);
        Assert.False(execution.IsCompleted);
        if (cancel) cancellation.Cancel();
        Assert.Equal(charged, memory.ReservedBytes);
        Assert.Equal(Encoding.UTF8.GetBytes(raw ? "xx\n" : "\"xx\"\n"), pending.ToArray());
        host.Release.SetResult();
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(execution.IsCompleted);
        if (cancel) Assert.Equal(charged, memory.ReservedBytes);
        else Assert.InRange(memory.ReservedBytes, pending.Length, charged);
        host.CloseRelease.SetResult();
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        }
        else
        {
            Assert.Equal(0, await execution);
            Assert.Equal(raw ? "xx\n" : "\"xx\"\n", inner.GetOutput(JqFileDescriptor.StdOut));
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
    }

    [Fact]
    public async Task Jq_GovernedOverlappingExecutionsShareParentDemand()
    {
        const long limit = 64 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        using IMemoryScope caller = memory.CreateScope();
        Assert.True(caller.TryReserve(1, out IMemoryReservation? retained));
        using (retained)
        {
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", "\"xx\"")));
            var first = new MemoryBorrowHost(new MockFileSystem());
            var second = new MemoryBorrowHost(new MockFileSystem());
            Task<int> firstCall = command.ExecuteAsync(first, Utf8RegexAdapter.Instance, caller, CancellationToken.None);
            await first.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task<int> secondCall = command.ExecuteAsync(second, Utf8RegexAdapter.Instance, caller, CancellationToken.None);
            await second.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long combined = memory.ReservedBytes;
            Assert.True(combined > 7);
            Assert.True(memory.TryReserve(limit - combined, out IMemoryReservation? pressure));
            using (pressure)
            {
                var refused = new MockFileSystem();
                Assert.Equal(5, await command.ExecuteAsync(refused, Utf8RegexAdapter.Instance, caller, CancellationToken.None));
                Assert.Empty(refused.GetOutput(JqFileDescriptor.StdOut));
                Assert.Equal("jq: memory budget exceeded\n", refused.GetOutput(JqFileDescriptor.StdErr));
                Assert.Equal(limit, memory.ReservedBytes);
                first.Release.SetResult();
                second.Release.SetResult();
                Assert.Equal(new[] { 0, 0 }, await Task.WhenAll(firstCall, secondCall));
                Assert.Equal(1 + pressure.Bytes, memory.ReservedBytes);
                Assert.Equal(1, retained.Bytes);
            }
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(limit, memory.PeakBytes);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("--build-configuration")]
    public async Task Jq_GovernedInformationOutputRefusalIsTerminal(string option)
    {
        var memory = new BenchmarkMemoryBudget(1);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", option)));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedReservationRejectsResizeAfterAncestorDisposal()
    {
        var memory = new BenchmarkMemoryBudget(8);
        using var owner = memory.CreateScope();
        using var reservation = new JqMemoryReservation(owner);
        reservation.Resize(8);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Throws<ObjectDisposedException>(() => reservation.Resize(1));
        Assert.Throws<ObjectDisposedException>(() => reservation.Resize(8));
        reservation.Dispose();
        Assert.True(memory.TryReserve(8, out IMemoryReservation? reused));
        reused.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class MemoryBorrowHost(MockFileSystem inner) : IJqHost
    {
        internal bool DelayCleanup { get; init; }
        internal JqFileDescriptor DelayedDescriptor { get; init; } = JqFileDescriptor.StdOut;
        internal JqAppendResult? StderrResult { get; init; }
        internal TaskCompletionSource<ReadOnlyMemory<byte>> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor,
            Memory<byte> buffer, CancellationToken cancellationToken) =>
            inner.ReadBytesAsync(descriptor, buffer, cancellationToken);

        public async Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor,
            ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (descriptor == DelayedDescriptor && !Ready.Task.IsCompleted)
            {
                Ready.SetResult(content);
                // Deliberately ignore cancellation while borrowing the bytes.
                await Release.Task.ConfigureAwait(false);
            }
            JqAppendResult result = await inner.AppendWhileOpenAsync(descriptor, content, cancellationToken).ConfigureAwait(false);
            return descriptor == JqFileDescriptor.StdErr && StderrResult is { } stderrResult ? stderrResult : result;
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(path, cancellationToken);

        public async Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (DelayCleanup)
            {
                Closing.TrySetResult();
                await CloseRelease.Task.ConfigureAwait(false);
            }
            return await inner.CloseDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
    }
}
