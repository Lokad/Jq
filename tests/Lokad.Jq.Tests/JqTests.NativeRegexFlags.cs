using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("test(\"x\";$flags)", "true\n")]
    [InlineData("match(\"x\";$flags)|.string", "X\n")]
    [InlineData("scan(\"x\";$flags)", "X\n")]
    [InlineData("[splits(\"x\";$flags)]|length", "2\n")]
    [InlineData("split(\"x\";$flags)|length", "2\n")]
    [InlineData("sub(\"x\";\"y\";$flags)", "y\n")]
    [InlineData("gsub(\"x\";\"y\";$flags)", "y\n")]
    public async Task Jq_NativeRegexFlagsNeedNoTranscodingOrGlobalFlagCopy(string operation, string expected)
    {
        const long limit = 2 * 1024 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null)
                Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 8192, out pressure));
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nrc", "--arg", "flags", new string('i', 1024 * 1024),
            "\"ready\", (\"X\"|" + operation + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("ready\n" + expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_NativeRegexModifierDiagnosticRefusalRemainsTerminal()
    {
        const long limit = 2 * 1024 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null)
                Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 8192, out pressure));
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "flags", "q" + new string('i', 1024 * 1024),
            "\"ready\", (try (\"X\"|gsub(\"x\";\"y\";$flags)) catch \"caught\")")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_NativeRegexFlagsObserveCancellationBeforeScanning()
    {
        JqString flags = NativeFixtures.Create(new string('i', 1024 * 1024));
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(cancellation.Token);
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() =>
            JqRegexOptions.ParseOrThrow(flags, JqRegexGlobalFlag.Prefix, budget));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public void Jq_NativeRegexModifierDiagnosticChecksCancellationAfterAdmission()
    {
        JqString flags = NativeFixtures.Create("q" + new string('i', 1024 * 1024));
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(null, hooked, cancellation.Token))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var error = Assert.Throws<OperationCanceledException>(() =>
                JqRegexOptions.ParseOrThrow(flags, JqRegexGlobalFlag.Suffix, budget));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }
}
