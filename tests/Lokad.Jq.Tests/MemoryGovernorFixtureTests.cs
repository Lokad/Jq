using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class MemoryGovernorFixtureTests
{
    [Fact]
    public void BenchmarkHostBudgetPreservesZeroRefusalResizeAndAncestorDisposal()
    {
        var root = new BenchmarkMemoryBudget(8);
        Assert.True(root.TryReserve(0, out var zero));
        using (zero)
        using (var parent = root.CreateScope())
        using (var child = parent.CreateScope())
        {
            Assert.Equal(0, root.ReservedBytes);
            Assert.True(child.TryReserve(6, out var reservation));
            using (reservation)
            {
                Assert.False(child.TryReserve(3, out var refused));
                Assert.Null(refused);
                Assert.False(reservation.TryResize(9));
                Assert.Equal(6, reservation.Bytes);
                Assert.Throws<ArgumentOutOfRangeException>(() => reservation.TryResize(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => child.TryReserve(-1, out _));
                parent.Dispose();
                Assert.Equal(0, root.ReservedBytes);
                Assert.Equal(0, root.ActiveScopes);
                Assert.Equal(2, root.PeakScopes);
                Assert.Equal(0, reservation.Bytes);
                Assert.Throws<ObjectDisposedException>(() => reservation.TryResize(6));
                Assert.Throws<ObjectDisposedException>(() => child.TryReserve(0, out _));
                Assert.Throws<ObjectDisposedException>(() => child.CreateScope());
            }
        }
        Assert.True(root.TryReserve(8, out var reused));
        reused.Dispose();
        reused.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task BenchmarkHostBudgetSharesConcurrentScopesAndReturnsAllAdmission()
    {
        var root = new BenchmarkMemoryBudget(128);
        using var parent = root.CreateScope();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        Task[] tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            using var child = parent.CreateScope();
            Assert.True(child.TryReserve(16, out var reservation));
            using (reservation)
            {
                if (Interlocked.Increment(ref count) == 8) ready.SetResult();
                await release.Task;
            }
        })).ToArray();
        start.SetResult();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(128, root.ReservedBytes);
            Assert.Equal(9, root.ActiveScopes);
            Assert.Equal(9, root.PeakScopes);
            Assert.False(parent.TryReserve(1, out var refused));
            Assert.Null(refused);
            Assert.Equal(128, root.ReservedBytes);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(128, root.PeakBytes);
        Assert.Equal(1, root.ActiveScopes);
        parent.Dispose();
        Assert.Equal(0, root.ActiveScopes);
    }
}
