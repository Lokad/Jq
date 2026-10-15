using System.Globalization;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
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
