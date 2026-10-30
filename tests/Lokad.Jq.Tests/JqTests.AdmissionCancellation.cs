using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("repeat-one")]
    [InlineData("repeat-many")]
    [InlineData("append")]
    [InlineData("finish")]
    [InlineData("implode")]
    [InlineData("parse-json")]
    [InlineData("metadata")]
    [InlineData("scope")]
    public void Jq_AdmissionCancellationStopsConstructionAfterSuccessfulAdmission(string operation)
    {
        const int length = 65536;
        string hostText = new('x', length);
        JqValue text = NativeFixtures.Create(hostText);
        var builder = operation == "finish" ? new StringBuilder(new string('x', length)) : new StringBuilder();
        var points = new JqArray();
        if (operation == "implode") for (int index = 0; index < length; index++) points.Add(65);
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, hooked, cancellation.Token))
        using (var scope = new JqScopeStorage(budget))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<OperationCanceledException>(Construct);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.InRange(allocated, 0, 32 * 1024);

            void Construct()
            {
                switch (operation)
                {
                    case "add": _ = runtime.Add(text, text); break;
                    case "repeat-one": _ = runtime.Multiply(NativeFixtures.Create("x"), NativeFixtures.Create(length)); break;
                    case "repeat-many": _ = runtime.Multiply(text, NativeFixtures.Create(2)); break;
                    case "append": budget.Append(builder, hostText); break;
                    case "finish": _ = budget.Finish(builder); break;
                    case "implode": _ = runtime.Implode(points); break;
                    case "parse-json": _ = runtime.ParseJson(hostText); break;
                    case "metadata": budget.ReserveMetadata(2 * length, 1); break;
                    case "scope": scope.Reserve(2 * length, 1); break;
                    default: throw new InvalidOperationException("Unknown construction test.");
                }
            }
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_AdmissionCancellationKeepsThePreviousBufferWhenReplacementIsCanceled()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var cancellation = new CancellationTokenSource();
        bool armed = false;
        var hooked = new AdmissionCallbackBudget(memory, () => { if (armed) cancellation.Cancel(); });
        byte[] buffer = [];
        using (var owner = new JqMemoryReservation(hooked))
        {
            owner.ReplaceBuffer(ref buffer, 8, cancellation.Token);
            buffer.AsSpan().Fill(42);
            byte[] previous = buffer;
            armed = true;
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<OperationCanceledException>(() => owner.ReplaceBuffer(ref buffer, 65536, cancellation.Token));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.InRange(allocated, 0, 32 * 1024);
            Assert.Same(previous, buffer);
            Assert.All(buffer, value => Assert.Equal(42, value));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_AdmissionCancellationDoesNotReplaceARefusedBufferAdmission()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        bool armed = false;
        var hooked = new AdmissionCallbackBudget(memory, () => { if (armed) cancellation.Cancel(); });
        byte[] buffer = [];
        using (var owner = new JqMemoryReservation(hooked))
        {
            owner.ReplaceBuffer(ref buffer, 8, cancellation.Token);
            byte[] previous = buffer;
            armed = true;
            var failure = Assert.Throws<JqQuotaException>(() => owner.ReplaceBuffer(ref buffer, 65536, cancellation.Token));
            Assert.True(failure.IsMemoryAdmission);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Same(previous, buffer);
            Assert.Equal(8, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_AdmissionCancellationStopsHostStringEncoding()
    {
        string text = new('x', 65536);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, hooked, cancellation.Token))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<OperationCanceledException>(() => JqString.FromHost(text, budget));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
            Assert.Equal(80 + text.Length, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_AdmissionCancellationStopsObjectComparisonScratchAllocation()
    {
        var input = new JqObject(NativeFixtures.Budget());
        for (int index = 0; index < 8192; index++) input.Add("key" + index, 0);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, hooked, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<OperationCanceledException>(() => runtime.Compare(input, input));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
            Assert.Equal(0, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_AdmissionCancellationStopsReadAllBeforeRequestingInput()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        var host = new MockFileSystem();
        host.SetStandardInput("input");
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, hooked, cancellation.Token))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await ((IJqHost)host).TryReadAllBytesAsync(JqFileDescriptor.StdIn, budget, cancellation.Token));
            Assert.Equal(0, host.ReadBytesCallCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_AdmissionCancellationStopsDiagnosticAllocationAndWrites()
    {
        string text = new('x', 1024 * 1024);
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        var host = new MockFileSystem();
        long before = GC.GetAllocatedBytesForCurrentThread();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JqDiagnosticWriter.TryWriteAsync(host,
            JqFileDescriptor.StdErr, [text], true, hooked, cancellation.Token));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        Assert.Equal(0, host.AppendCallCount);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("\"x\" * 1048576")]
    [InlineData("\"ab\" * 524288")]
    [InlineData("$text")]
    public async Task Jq_AdmissionCancellationRetainsPrefixWithoutAllocatingCanceledOutput(string expression)
    {
        var memory = new BenchmarkMemoryBudget(16 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        bool armed = false;
        var hooked = new AdmissionCallbackBudget(memory, () =>
        {
            if (armed && memory.ReservedBytes >= 1024 * 1024) cancellation.Cancel();
        });
        var host = new MockFileSystem();
        long before = 0;
        host.BeforeByteAppend = () =>
        {
            if (host.AppendCallCount != 0) return;
            before = GC.GetAllocatedBytesForCurrentThread();
            armed = true;
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "text", new string('x', 1024 * 1024),
            "\"ready\", (" + expression + ")")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, hooked, cancellation.Token));
        Assert.True(armed);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256 * 1024);
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.AppendCallCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData("try (\"x\" * 1048576) catch \"caught\"")]
    [InlineData("(\"x\" * 1048576)?")]
    public async Task Jq_AdmissionCancellationPreservesTerminalRefusalWhenCancellationAlsoArrives(string operation)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        bool armed = false;
        var hooked = new AdmissionCallbackBudget(memory, () => { if (armed) cancellation.Cancel(); });
        var host = new MockFileSystem { BeforeByteAppend = () => armed = true };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "\"ready\", (" + operation + ")")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, hooked, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
