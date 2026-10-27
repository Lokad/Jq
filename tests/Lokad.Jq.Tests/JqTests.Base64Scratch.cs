using System.Text;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(65536)]
    public void Jq_Base64ScratchRetainsOnlyTheEncodedOutputAfterConversion(int length)
    {
        string text = new('x', length);
        string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var counted = new CountingMemoryBudget(memory);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None))
        {
            string encoded = new JqRuntime(budget).Format("base64", JsonValue.Create(text));
            Assert.Equal(expected, encoded);
            Assert.Equal(2L * expected.Length, memory.ReservedBytes);
            Assert.Equal(2L * expected.Length + length, memory.PeakBytes);
            Assert.Equal(length == 0 ? 0 : 2, counted.Counts.Reservations + counted.Counts.Resizes);
            Assert.Equal(length == 0 ? 0 : 1, counted.Counts.ReservationDisposals);
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(counted.Counts.Reservations, counted.Counts.ReservationDisposals);
        Assert.Equal(0, counted.Counts.BytesReads);
    }

    [Theory]
    [InlineData("a", "YQ==")]
    [InlineData("ab", "YWI=")]
    [InlineData("abc", "YWJj")]
    [InlineData("é", "w6k=")]
    [InlineData("lone-surrogate", "77+9")]
    public void Jq_Base64ScratchPreservesPaddingUnicodeAndHostReplacement(string text, string expected)
    {
        if (text == "lone-surrogate") text = new string((char)0xD800, 1);
        var memory = new BenchmarkMemoryBudget(16 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
            Assert.Equal(expected, new JqRuntime(budget).Format("base64", JsonValue.Create(text)));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(174767, 0)]
    [InlineData(240303, 174768)]
    public void Jq_Base64ScratchPreflightsOutputAndRealOverlapBeforeEncoding(long allowance, long retained)
    {
        JsonNode input = JsonValue.Create(new string('x', 65536));
        var memory = new BenchmarkMemoryBudget(allowance);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<JqQuotaException>(() => runtime.Format("base64", input));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
            Assert.True(failure.IsMemoryAdmission);
            Assert.Equal(retained, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_Base64ScratchObservesCancellationAfterDirectScratchAdmission()
    {
        JsonNode input = JsonValue.Create(new string('x', 65536));
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var hooked = new AdmissionCallbackBudget(memory, () => { if (++calls == 2) cancellation.Cancel(); });
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, hooked, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<OperationCanceledException>(() => runtime.Format("base64", input));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(174768, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_Base64ScratchRoundTripFitsItsActiveStorageAllowance()
    {
        var memory = new BenchmarkMemoryBudget(512 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "\"ready\", ((\"x\"*65536)|@base64|@base64d|length)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("ready\n65536\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_Base64ScratchStreamsWithBoundedMemoryAndStopsOnClosure(bool close)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = !close };
        host.SetStandardInput(string.Concat(Enumerable.Repeat(new string('é', 1024) + "\n", 128)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rr", "@base64|length")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("2732\n", close ? 1 : 128)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(close ? 1 : 128, host.AppendCallCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData("try ($text|@base64) catch \"caught\"", 4096)]
    [InlineData("($text|@base64)?", 4096)]
    [InlineData("try ($text|@base64) catch \"caught\"", 200000)]
    [InlineData("($text|@base64)?", 200000)]
    public async Task Jq_Base64ScratchRefusalPreservesEarlierOutputAndBypassesHandlers(string operation, long remaining)
    {
        const long limit = 512 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(memory.TryReserve(limit - memory.ReservedBytes - remaining, out pressure));
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "text", new string('x', 65536),
            "\"ready\", (" + operation + ")")));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Fact]
    public async Task Jq_Base64ScratchCancellationPreservesEarlierResults()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat(new string('é', 1024) + "\n", 128)));
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 3) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rr", "@base64|length")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal("2732\n2732\n2732\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
