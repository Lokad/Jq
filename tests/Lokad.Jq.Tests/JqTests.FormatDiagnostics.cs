using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("try @bogus catch .", "bogus is not a valid format\n")]
    [InlineData("try format(\"bogus\") catch .", "bogus is not a valid format\n")]
    [InlineData("try format(\"\") catch .", " is not a valid format\n")]
    [InlineData("try format(123) catch .", "number (123) is not a valid format\n")]
    public async Task Jq_FormatDiagnosticsShareNativeTextAcrossBothEntryPoints(string filter, string expected)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_FormatDiagnosticsAdmitTheEntireEscapingErrorText()
    {
        string name = new('z', 65536);
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            var failure = Assert.Throws<JqException>(() => runtime.Format(name, new JsonArray()));
            Assert.Equal(name + " is not a valid format", failure.Message);
            Assert.True(memory.ReservedBytes >= 2L * failure.Message.Length);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(65536, "try format($name) catch length")]
    [InlineData(65536, "format($name)?")]
    [InlineData(1048576, "try format($name) catch length")]
    [InlineData(1048576, "format($name)?")]
    public async Task Jq_FormatDiagnosticsRefusalPreventsLargeCopiesAndPreservesPrefix(int length, string operation)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "name", new string('z', length),
            "\"ready\", (" + operation + ")")));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256 * 1024);
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Fact]
    public void Jq_FormatDiagnosticsObserveCancellationAfterAdmission()
    {
        string name = new('z', 65536);
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, hooked, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<OperationCanceledException>(() => runtime.Format(name, null));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_FormatDiagnosticsRefusalRetainsPrecedenceOverCancellation()
    {
        var memory = new BenchmarkMemoryBudget(0);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, hooked, cancellation.Token))
        {
            var failure = Assert.Throws<JqQuotaException>(() => new JqRuntime(budget).Format("missing", null));
            Assert.True(failure.IsMemoryAdmission);
            Assert.True(cancellation.IsCancellationRequested);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FormatDiagnosticsKeepCompletePayloadsAcrossCaptures()
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        string name = new('z', 65536);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--arg", "name", name,
            "[range(3)|try format($name) catch .] as $errors | $errors|map(length)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("[65558,65558,65558]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_FormatDiagnosticsReuseCreditAcrossSmallRecords()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var counted = new CountingMemoryBudget(memory);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            for (int record = 0; record < 2500; record++)
            {
                budget.BeginRecord();
                Assert.Equal("missing is not a valid format", Assert.Throws<JqException>(() => runtime.Format("missing", null)).Message);
            }
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 8);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_FormatDiagnosticsStreamWithBoundedMemoryAndStopOnClosure(bool close)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = !close };
        host.SetStandardInput(string.Concat(Enumerable.Repeat("bogus\n", 2500)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rr", "try format(.) catch length")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("27\n", close ? 1 : 2500)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(close ? 1 : 2500, host.AppendCallCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Fact]
    public async Task Jq_FormatDiagnosticsCancellationPreservesEarlierResults()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("bogus\n", 2500)));
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 3) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rr", "try format(.) catch length")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal("27\n27\n27\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
