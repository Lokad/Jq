using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void Jq_Base64ScratchReusesRecordCreditForRepeatedSmallEncodes(int length)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var counted = new CountingMemoryBudget(memory);
        JqValue input = NativeFixtures.Create(new string('x', length));
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            for (int record = 0; record < 2500; record++)
            {
                budget.BeginRecord();
                Assert.Equal(4 * ((length + 2) / 3), runtime.FormatValue("base64", input).GetValue<string>().Length);
            }
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 8);
            Assert.InRange(memory.PeakBytes, 1, 256);
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(counted.Counts.Reservations, counted.Counts.ReservationDisposals);
        Assert.Equal(0, counted.Counts.Refusals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(65536)]
    public void Jq_Base64EncodingAdmitsOnlyItsNativeOutput(int length)
    {
        string text = new('x', length);
        string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var counted = new CountingMemoryBudget(memory);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None))
        {
            string encoded = new JqRuntime(budget).FormatValue("base64", NativeFixtures.Create(text)).GetValue<string>();
            Assert.Equal(expected, encoded);
            Assert.Equal(80L + expected.Length, memory.ReservedBytes);
            Assert.Equal(80L + expected.Length, memory.PeakBytes);
            Assert.Equal(1, counted.Counts.Reservations + counted.Counts.Resizes);
            Assert.Equal(0, counted.Counts.ReservationDisposals);
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
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None))
            Assert.Equal(expected, new JqRuntime(budget).FormatValue("base64", NativeFixtures.Create(text)).GetValue<string>());
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(87463, false, 0)]
    [InlineData(153079, true, 65616)]
    public void Jq_Base64PreflightsNativeOutputAndOwnedInputOverlap(long allowance, bool ownInput, long retained)
    {
        var memory = new BenchmarkMemoryBudget(allowance);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None))
        {
            JqValue input = ownInput ? JqString.FromHost(new string('x', 65536), budget)
                : NativeFixtures.Create(new string('x', 65536));
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<JqQuotaException>(() => runtime.FormatValue("base64", input).GetValue<string>());
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
            Assert.True(failure.IsMemoryAdmission);
            Assert.Equal(retained, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_Base64ObservesCancellationAfterNativeOutputAdmission()
    {
        JqValue input = NativeFixtures.Create(new string('x', 65536));
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, hooked, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<OperationCanceledException>(() => runtime.FormatValue("base64", input).GetValue<string>());
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(87464, memory.ReservedBytes);
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
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
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
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("2732\n", close ? 1 : 128)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(close ? 1 : 128, host.AppendCallCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData("try ($text|@base64) catch \"caught\"", 4096)]
    [InlineData("($text|@base64)?", 4096)]
    [InlineData("try ($text|@base64) catch \"caught\"", 87463)]
    [InlineData("($text|@base64)?", 87463)]
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
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.Equal("2732\n2732\n2732\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
