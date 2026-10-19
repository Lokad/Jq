using System.Text;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void Jq_GovernedMixedSerializationReconcilesFinishedGrowthBeforeReuse(int count)
    {
        var root = new TestMemoryBudget(8192);
        var memory = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(JqExecutionPolicy.Default, memory, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            JsonNode small = JsonValue.Create("a");
            JsonNode large = JsonValue.Create(new string('a', 1194));
            byte[] expected = Encoding.UTF8.GetBytes("\"" + new string('a', 1194) + "\"");
            for (int i = 0; i < 32; i++)
            {
                using var warm = runtime.SerializeUtf8(small, false, null, false);
            }
            MemoryCallCounts warmed = memory.Counts;
            for (int i = 0; i < count; i++)
            {
                using var result = runtime.SerializeUtf8(i % 2 == 0 ? small : large, false, null, false);
                Assert.Equal(i % 2 == 0 ? "\"a\""u8.ToArray() : expected, result.Value.ToArray());
            }
            Assert.Equal(warmed.Reservations, memory.Counts.Reservations);
            Assert.InRange(memory.Counts.Resizes - warmed.Resizes, 1, 4);
            Assert.InRange(root.ReservedBytes, 257, 4096);
            Assert.True(root.PeakBytes > root.ReservedBytes);
            Assert.Equal(0, memory.Counts.BytesReads);
            Assert.Equal(0, memory.Counts.SameSizeResizes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedSerializerReconciliationCannotBypassReplacementPeak()
    {
        var root = new TestMemoryBudget(4096);
        using var budget = new JqBudget(JqExecutionPolicy.Default, root, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        for (int i = 0; i < 32; i++)
        {
            using var warm = runtime.SerializeUtf8(JsonValue.Create("a"), false, null, false);
        }
        Assert.True(0 < root.ReservedBytes && root.ReservedBytes < 4096);
        var failure = Assert.Throws<JqQuotaException>(() =>
            runtime.SerializeUtf8(JsonValue.Create(new string('a', 1194)), false, null, false));
        Assert.True(failure.IsMemoryAdmission);
        Assert.Equal("memory budget exceeded", failure.Message);
        Assert.Equal(0, root.ReservedBytes);
        using (var next = runtime.SerializeUtf8(JsonValue.Create("a"), false, null, false))
            Assert.Equal("\"a\""u8.ToArray(), next.Value.ToArray());
    }

    [Fact]
    public void Jq_GovernedReconciledSerializerCreditStaysChargedAndReturnDoesNotResize()
    {
        const int limit = 8192;
        var root = new TestMemoryBudget(limit);
        var memory = new CountingMemoryBudget(root);
        using var budget = new JqBudget(JqExecutionPolicy.Default, memory, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        for (int i = 0; i < 32; i++)
        {
            using var warm = runtime.SerializeUtf8(JsonValue.Create("a"), false, null, false);
        }
        var result = runtime.SerializeUtf8(JsonValue.Create(new string('a', 1194)), false, null, false);
        long active = root.ReservedBytes;
        MemoryCallCounts handedOff = memory.Counts;
        result.Dispose();
        Assert.Equal(handedOff.Resizes, memory.Counts.Resizes);
        Assert.Equal(active, root.ReservedBytes);
        Assert.InRange(active, 257, 4096);
        Assert.True(root.TryReserve(limit - active, out var competing));
        using (competing)
        {
            Assert.False(root.TryReserve(1, out var refused));
            Assert.Null(refused);
            budget.Dispose();
            Assert.Equal(competing.Bytes, root.ReservedBytes);
            Assert.True(root.TryReserve(active, out var available));
            available.Dispose();
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Jq_GovernedHotSerializationPreservesEncodingAndFormatting(bool ascii, bool pretty, bool tabs)
    {
        var root = new TestMemoryBudget(32 * 1024);
        using (var budget = new JqBudget(JqExecutionPolicy.Default, root, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            for (int i = 0; i < 32; i++)
            {
                using var warm = runtime.SerializeUtf8(JsonValue.Create("a"), false, null, false);
            }
            string text = new('é', 100);
            string escaped = ascii ? string.Concat(Enumerable.Repeat("\\u00e9", 100)) : text;
            string expected = pretty
                ? "[\n" + (tabs ? "\t" : "  ") + "\"" + escaped + "\"\n]"
                : "[\"" + escaped + "\"]";
            using var result = runtime.SerializeUtf8(new JsonArray(JsonValue.Create(text)), ascii, pretty ? 2 : null, tabs);
            Assert.Equal(Encoding.UTF8.GetBytes(expected), result.Value.ToArray());
            using var overlapping = runtime.SerializeUtf8(JsonValue.Create("next"), false, null, false);
            Assert.Equal("\"next\""u8.ToArray(), overlapping.Value.ToArray());
            Assert.Equal(Encoding.UTF8.GetBytes(expected), result.Value.ToArray());
        }
        Assert.Equal(0, root.ReservedBytes);
    }

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
    public void Jq_GovernedSerializationReusePreservesCancellationAndAdmission()
    {
        var memory = new TestMemoryBudget(4096);
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(JqExecutionPolicy.Default, memory, cancellation.Token);
        var runtime = new JqRuntime(budget);
        for (int i = 0; i < 64; i++)
        {
            using var warm = runtime.SerializeUtf8(JsonValue.Create("x"), false, null, false);
        }
        Assert.True(memory.ReservedBytes > 0);
        for (int i = 0; i < 1000; i++)
        {
            using var result = runtime.SerializeUtf8(JsonValue.Create("x"), false, null, false);
            Assert.InRange(memory.ReservedBytes, 1, 4096);
        }
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
