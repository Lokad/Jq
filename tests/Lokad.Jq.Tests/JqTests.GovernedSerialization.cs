using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_GovernedColdSerializationRefundsImmediately()
    {
        var memory = new TestMemoryBudget(256);
        using var budget = new JqBudget(JqExecutionPolicy.Default, memory, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        for (int i = 0; i < 10; i++)
        {
            using (var result = runtime.SerializeUtf8(JsonValue.Create("x"), false, null, false))
                Assert.Equal("\"x\""u8.ToArray(), result.Value.ToArray());
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Fact]
    public void Jq_GovernedSerializationKeepsOverlappingResultsDistinctAndDefersActiveRefunds()
    {
        var memory = new TestMemoryBudget(32 * 1024);
        using var budget = new JqBudget(JqExecutionPolicy.Default, memory, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        for (int i = 0; i < 64; i++)
        {
            using var warm = runtime.SerializeUtf8(JsonValue.Create("warm"), false, null, false);
        }
        using var first = runtime.SerializeUtf8(JsonValue.Create("one"), false, null, false);
        long one = memory.ReservedBytes;
        using var second = runtime.SerializeUtf8(JsonValue.Create("two"), false, null, false);
        Assert.True(memory.ReservedBytes > one);
        Assert.Equal("\"one\""u8.ToArray(), first.Value.ToArray());
        first.Dispose();
        using (var third = runtime.SerializeUtf8(JsonValue.Create("three"), false, null, false))
        {
            first.Dispose(); // A stale owner cannot return the reused backing storage.
            Assert.Equal("\"three\""u8.ToArray(), third.Value.ToArray());
            Assert.Equal("\"two\""u8.ToArray(), second.Value.ToArray());
        }
        budget.Dispose();
        Assert.Equal(one, memory.ReservedBytes);
        Assert.Equal("\"two\""u8.ToArray(), second.Value.ToArray());
        Assert.Throws<ObjectDisposedException>(() => runtime.SerializeUtf8(JsonValue.Create("x"), false, null, false));
        second.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedSerializationDropsOversizedAndFailedBuffers()
    {
        var memory = new TestMemoryBudget(32 * 1024);
        using var budget = new JqBudget(JqExecutionPolicy.Default, memory, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        for (int i = 0; i < 64; i++)
        {
            using var warm = runtime.SerializeUtf8(JsonValue.Create("x"), false, null, false);
        }
        using (var large = runtime.SerializeUtf8(JsonValue.Create(new string('x', 5000)), false, null, false))
            Assert.Equal(5002, large.Value.Length);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.True(memory.TryReserve(32 * 1024 - 512, out var pressure));
        using (pressure)
        {
            using (var small = runtime.SerializeUtf8(JsonValue.Create("x"), false, null, false))
                Assert.Equal("\"x\""u8.ToArray(), small.Value.ToArray());
            Assert.Throws<JqQuotaException>(() => runtime.SerializeUtf8(JsonValue.Create(new string('x', 1000)), false, null, false));
            Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedSerializationReusePreservesWorkAndCancellationGuards()
    {
        var memory = new TestMemoryBudget(4096);
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(new JqExecutionPolicy { MaximumAllocationWorkBytes = 10000 }, memory, cancellation.Token);
        var runtime = new JqRuntime(budget);
        for (int i = 0; i < 64; i++)
        {
            using var warm = runtime.SerializeUtf8(JsonValue.Create("x"), false, null, false);
        }
        Assert.True(memory.ReservedBytes > 0);
        Assert.Equal("allocation work allowance exceeded", Assert.Throws<JqQuotaException>(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                using var result = runtime.SerializeUtf8(JsonValue.Create("x"), false, null, false);
            }
        }).Message);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => runtime.SerializeUtf8(JsonValue.Create("x"), false, null, false));
        budget.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Jq_GovernedRepeatedSerializationReusesOnlyReturnedBuffers(int count)
    {
        var root = new TestMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "range(" + count + ") | \"x\" | tojson")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("\"x\"\n", count)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations, 1, 64);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }
}
