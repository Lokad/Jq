using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Jq_NativeDateErrorsKeepCompletePayloadsAndTerminalAdmission(bool iso, bool refused)
    {
        const long limit = 4 * 1024 * 1024;
        string input = new string('x', 1024 * 1024) + "é😀\0";
        string format = iso ? "%Y-%m-%dT%H:%M:%SZ" : "%Y";
        string operation = iso ? "fromdateiso8601" : "strptime(\"%Y\")";
        var memory = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null)
            {
                long headroom = refused ? 8192 : System.Text.Encoding.UTF8.GetByteCount(input) + 8192L;
                Assert.True(memory.TryReserve(limit - memory.ReservedBytes - headroom, out pressure));
            }
        };
        // Joined raw output borrows the payload, isolating its construction admission.
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nrj", "--arg", "value", input,
            "\"ready\", (try ($value | " + operation + ") catch .)")));
        Assert.Equal(refused ? 5 : 0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(refused ? "ready" : "readydate \"" + input + "\" does not match format \"" + format + "\"",
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(refused ? "jq: memory budget exceeded\n" : "", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_NativeDateErrorChecksCancellationAfterAdmission()
    {
        JqString text = NativeFixtures.Create(new string('x', 1024 * 1024));
        JqString format = NativeFixtures.Create("%Y");
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(null, hooked, cancellation.Token)))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var error = Assert.Throws<OperationCanceledException>(() => JqTime.Strptime(context, text, format));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }
}
