using System.Globalization;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_GovernedStorageCreditStaysChargedAndRefundsAtReconciliation()
    {
        var root = new TestMemoryBudget(16 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            storage.ReserveBytes(4096);
            storage.ReserveBytes(1);
            Assert.Equal(4097, storage.UsedBytes);
            Assert.Equal(8192, root.ReservedBytes);
            MemoryCallCounts admitted = memory.Counts;
            storage.ReserveBytes(2048);
            Assert.Equal(6145, storage.UsedBytes);
            Assert.Equal(admitted, memory.Counts);
            storage.Resize(1000);
            Assert.Equal(1000, root.ReservedBytes);
            Assert.Equal(1, memory.Counts.ShrinkingResizes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedStorageFallsBackToExactOnceAndPreservesFailedDemand()
    {
        var root = new TestMemoryBudget(7000);
        var memory = new CountingMemoryBudget(root);
        using (var storage = new JqStorage(memory))
        {
            storage.ReserveBytes(4096);
            storage.ReserveBytes(1);
            Assert.Equal(4097, root.ReservedBytes);
            Assert.Equal(1, memory.Counts.Refusals);
            long previousCalls = memory.Counts.Resizes;
            storage.ReserveBytes(1);
            Assert.Equal(previousCalls + 1, memory.Counts.Resizes);
            Assert.Equal(1, memory.Counts.Refusals);
            Assert.Throws<JqQuotaException>(() => storage.ReserveBytes(3000));
            Assert.Equal(4098, storage.UsedBytes);
            Assert.Equal(4098, root.ReservedBytes);
            storage.ReserveBytes(1);
            Assert.Equal(4099, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedKnownCapacityAdmissionsStayExactAndOverflowLeavesDemandUnchanged()
    {
        var root = new TestMemoryBudget(long.MaxValue);
        using (var storage = new JqStorage(root))
        {
            storage.ReserveBytes(4096);
            storage.ReserveBytes(1024);
            Assert.Equal(5120, root.ReservedBytes);
            storage.ReserveBytes(long.MaxValue - 5120);
            Assert.Throws<JqQuotaException>(() => storage.ReserveBytes(1));
            Assert.Equal(long.MaxValue, storage.UsedBytes);
            Assert.Equal(long.MaxValue, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(100000)]
    public async Task Jq_GovernedHtmlUsesWholeBuilderAdmissionForSmallAndLargeText(int count)
    {
        var root = new TestMemoryBudget(2 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        string value = new('a', count);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r",
            "--arg", "value", value, "$value | @html")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(value + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 32);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task Jq_GovernedMapBatchesNodeAndSlotAdmission(int count)
    {
        var root = new TestMemoryBudget(4 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        host.SetStandardInput("[" + string.Join(',', Enumerable.Range(0, count)) + "]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "map(. + 1)")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("[" + string.Join(',', Enumerable.Range(1, count)) + "]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, count / 8 + 200);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Jq_GovernedJsonOutputCallsFollowCapacityInsteadOfRecordCount(int count)
    {
        var root = new TestMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-c",
            "range(" + count.ToString(CultureInfo.InvariantCulture) + ")")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Range(0, count).Select(number =>
            number.ToString(CultureInfo.InvariantCulture) + "\n")), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
        MemoryCallCounts calls = memory.Counts;
        Assert.Equal(0, calls.BytesReads);
        Assert.Equal(0, calls.SameSizeResizes);
        Assert.InRange(calls.Reservations + calls.Resizes, 1, 32);
    }

    [Fact]
    public void Jq_GovernedBudgetUsesCachedAdmissionAndPreservesRefusalAndZeroCancellation()
    {
        var root = new TestMemoryBudget(6);
        var memory = new CountingMemoryBudget(root);
        using var cancellation = new CancellationTokenSource();
        using (var budget = new JqBudget(JqExecutionPolicy.Default, memory, cancellation.Token))
        {
            budget.ReserveBytes(3);
            MemoryCallCounts initial = memory.Counts;
            budget.ReserveBytes(0);
            budget.ReserveCloneStorage(JsonValue.Create(1));
            Assert.Equal(initial, memory.Counts);
            budget.ReserveBytes(2);
            Assert.Throws<JqQuotaException>(() => budget.ReserveBytes(2));
            Assert.Equal(5, root.ReservedBytes);
            budget.ReserveBytes(1);
            Assert.Equal(6, root.ReservedBytes);
            Assert.Equal(0, memory.Counts.BytesReads);
            Assert.Equal(1, memory.Counts.Refusals);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => budget.ReserveBytes(0));
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedJsonBufferKeepsRealReplacementPeaksWithoutNoopNotifications()
    {
        var root = new TestMemoryBudget(1024);
        var memory = new CountingMemoryBudget(root);
        using var budget = new JqBudget(JqExecutionPolicy.Default, memory, CancellationToken.None);
        using (var buffer = new JqJsonBuffer(budget))
        {
            buffer.Reset();
            Assert.Equal(0, memory.Counts.Reservations);
            buffer.GetSpan(3);
            buffer.ReleaseReplacedStorage();
            buffer.Reset();
            Assert.Equal(256, root.ReservedBytes);
            buffer.GetMemory(300);
            Assert.Equal(812, root.ReservedBytes);
            buffer.ReleaseReplacedStorage();
            buffer.ReleaseReplacedStorage();
            Assert.Equal(556, root.ReservedBytes);
            Assert.Equal(1, memory.Counts.ShrinkingResizes);
            Assert.Equal(0, memory.Counts.BytesReads);
            Assert.Equal(0, memory.Counts.SameSizeResizes);
        }
        Assert.Equal(812, root.PeakBytes);
        Assert.Equal(0, root.ReservedBytes);
    }
}
