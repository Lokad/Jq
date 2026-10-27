using System.Text;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_SerializationRetirementAdmitsOnlyTheCurrentReplacement(bool memoryLoan)
    {
        var root = new BenchmarkMemoryBudget(96 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None))
        using (var buffer = new JqJsonBuffer(budget))
        {
            int written = 0;
            for (int length = 256; length <= 32768; length *= 2)
            {
                Span<byte> span = memoryLoan ? buffer.GetMemory(length).Span : buffer.GetSpan(length);
                span[..length].Fill((byte)'x');
                buffer.Advance(length);
                written += length;
            }
            Assert.Equal(65280, written);
            Assert.Equal(written, buffer.WrittenMemory.Length);
            Assert.Equal(97792, root.PeakBytes);
            Assert.Equal(8, memory.Counts.Reservations + memory.Counts.Resizes);
            buffer.ReleaseReplacedStorage();
            Assert.Equal(buffer.Capacity, root.ReservedBytes);
            Assert.Equal(9, memory.Counts.Reservations + memory.Counts.Resizes);
            Assert.All(buffer.WrittenMemory.ToArray(), value => Assert.Equal((byte)'x', value));
            Assert.Equal(0, memory.Counts.Refusals);
            Assert.Equal(0, memory.Counts.SameSizeResizes);
            Assert.Equal(0, memory.Counts.BytesReads);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_SerializationRetirementKeepsFramingBodyAdmittedThroughItsCopy()
    {
        var root = new BenchmarkMemoryBudget(769);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
        using (var buffer = new JqJsonBuffer(budget))
        {
            buffer.GetSpan(256)[..256].Fill((byte)'x');
            buffer.Advance(256);
            ReadOnlyMemory<byte> body = buffer.WrittenMemory;
            buffer.Reset();
            Span<byte> frame = buffer.GetSpan(257);
            Assert.Equal(769, root.ReservedBytes);
            Assert.Equal(256, body.Length);
            body.Span.CopyTo(frame[1..]);
            frame[0] = 30;
            buffer.Advance(257);
            buffer.ReleaseReplacedStorage();
            Assert.Equal(513, root.ReservedBytes);
            Assert.Equal(30, buffer.WrittenMemory.Span[0]);
            Assert.All(buffer.WrittenMemory[1..].ToArray(), value => Assert.Equal((byte)'x', value));
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_SerializationRetirementKeepsNecessaryReplacementRefusalTerminal(bool memoryLoan)
    {
        var root = new BenchmarkMemoryBudget(768);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
        using (var buffer = new JqJsonBuffer(budget))
        {
            buffer.GetSpan(256)[..256].Fill((byte)'x');
            buffer.Advance(256);
            ReadOnlyMemory<byte> body = buffer.WrittenMemory;
            buffer.Reset();
            var failure = Assert.Throws<JqQuotaException>(() =>
            {
                if (memoryLoan) buffer.GetMemory(257);
                else buffer.GetSpan(257);
            });
            Assert.True(failure.IsMemoryAdmission);
            Assert.Equal(256, root.ReservedBytes);
            Assert.All(body.ToArray(), value => Assert.Equal((byte)'x', value));
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_SerializationRetirementReconcilesColdAndWarmResults(bool warm)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            if (warm)
                for (int index = 0; index < 32; index++)
                    using (runtime.SerializeUtf8(JsonValue.Create(0), false, null, false)) { }
            var input = new JsonArray();
            for (int index = 0; index < 10000; index++) input.Add(index);
            using (var result = runtime.SerializeUtf8(input, false, null, false))
            {
                Assert.Equal(48891, result.Value.Length);
                Assert.Equal(69632, root.ReservedBytes);
                Assert.Equal(input.ToJsonString(), Encoding.UTF8.GetString(result.Value.Span));
                Assert.Equal(0, memory.Counts.Refusals);
                Assert.Equal(0, memory.Counts.SameSizeResizes);
            }
            Assert.Equal(0, root.ReservedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_SerializationRetirementKeepsLargePendingOutputOwned(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        string value = "[" + string.Join(',', Enumerable.Range(0, 10000)) + "]";
        string expected = "\u001e" + value + "\n";
        var inner = new MockFileSystem();
        inner.AddFile("/records", "\u001e" + value + "\n");
        var host = new MemoryBorrowHost(inner);
        var root = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--seq", ".", "/records")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), pending.ToArray());
        long charged = root.ReservedBytes;
        Assert.True(charged > pending.Length);
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, root.ReservedBytes);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), pending.ToArray());
        host.Release.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else Assert.Equal(0, await execution);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
