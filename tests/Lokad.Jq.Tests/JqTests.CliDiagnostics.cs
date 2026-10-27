using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("argjson")]
    [InlineData("rawfile")]
    [InlineData("slurpfile")]
    public async Task Jq_CliDiagnosticsAdmitLongTextBeforeConstructingIt(string kind)
    {
        string payload = new('x', 1024 * 1024);
        string[] arguments = kind switch
        {
            "unknown" => ["--" + payload],
            "argjson" => ["--argjson", payload, "{", "-nc", "1"],
            "rawfile" => ["--rawfile", "v", "/" + payload, "-nc", "1"],
            _ => ["--slurpfile", payload, "/bad", "-nc", "1"]
        };
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, []);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        var warm = new MockFileSystem(); warm.AddFile("/bad", "!");
        Assert.Equal(5, await command.ExecuteAsync(warm, Utf8RegexAdapter.Instance, new BenchmarkMemoryBudget(64 * 1024), CancellationToken.None));
        var host = new MockFileSystem(); host.AddFile("/bad", "!");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(execution.IsCompletedSuccessfully);
        Assert.Equal(5, await execution);
        Assert.InRange(allocated, 0, 64 * 1024);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(kind == "slurpfile" ? 1 : 0, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_CliDiagnosticsKeepCompleteAdmittedText(bool missingFile)
    {
        string text = new('x', 4096);
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        string[] arguments = missingFile ? ["--rawfile", "v", "/" + text, "-nc", "1"] : ["--" + text];
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, []);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(2, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(missingFile ? "jq: Bad JSON in --rawfile v /" + text + ": cannot open /" + text + "\n"
            : "jq: Unknown option --" + text + "\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.True(memory.PeakBytes > 2L * text.Length);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_CliDiagnosticsObserveCancellationAfterTextAdmission(bool missingFile)
    {
        using var cancellation = new CancellationTokenSource();
        string text = new('x', 1024 * 1024);
        var root = new BenchmarkMemoryBudget(16 * 1024 * 1024);
        var memory = new DiagnosticCancellationBudget(root, cancellation);
        var host = new MockFileSystem();
        string[] arguments = missingFile ? ["--rawfile", "v", "/" + text, "-nc", "1"] : ["--" + text];
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, root.ActiveScopes);
    }

    [Theory]
    [InlineData("", "2", "[\n  1\n]\n", 0)]
    [InlineData("+", "7", "[\n       1\n]\n", 0)]
    [InlineData("-", "1", "[\n\t1\n]\n", 0)]
    [InlineData("-", "0", "[\n1\n]\n", 0)]
    [InlineData("", "8", "", 2)]
    [InlineData("", "2x", "", 2)]
    public async Task Jq_CliIndentScansBorrowedZeroPadding(string sign, string tail, string output, int status)
    {
        string width = sign + new string('0', 256 * 1024) + tail;
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--indent", width, "-n", "[1]")));
        Assert.Equal(status, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(status == 0 ? "" : "jq: --indent takes a number between -1 and 7\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.PeakBytes, 1, 8192);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_CliIndentChecksCancellationAfterFetchingWidth()
    {
        using var cancellation = new CancellationTokenSource();
        var args = new WidthCancellationArguments(["--indent", new string('0', 256 * 1024) + "2", "-n", "[1]"], cancellation);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await JqCommandLineParser.ParseAsync(
                new MockFileSystem(), args, JqPath.Root, JqFileDescriptor.StdIn, JqFileDescriptor.StdOut,
                JqFileDescriptor.StdErr, [], null, budget));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("v")]
    [InlineData("ENV")]
    [InlineData("ARGS")]
    public async Task Jq_CliBindingMembershipKeepsTheFirstNullValue(string name)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--argjson", name, "null",
            "--argjson", name, "{", "--rawfile", name, "/missing", "$ARGS.named")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("{\"" + name + "\":null}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_CliBindingMembershipUsesOneRetainedTable()
    {
        string[] arguments = ["-nc", .. Enumerable.Range(0, 512).SelectMany(i => new[] { "--arg", "v" + i, "x" }), "$ARGS.named|length"];
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("512\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class WidthCancellationArguments(string[] values, CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => values.Length;
        public string this[int index]
        {
            get
            {
                if (index == 1) cancellation.Cancel();
                return values[index];
            }
        }
        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DiagnosticCancellationBudget(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryScope
    {
        public IMemoryScope CreateScope() => new DiagnosticCancellationBudget(inner.CreateScope(), cancellation);
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out var admitted)) { reservation = null; return false; }
            reservation = new ObservedReservation(admitted, cancellation);
            if (bytes > 128 * 1024) cancellation.Cancel();
            return true;
        }
        public void Dispose() { if (inner is IMemoryScope scope) scope.Dispose(); }

        private sealed class ObservedReservation(IMemoryReservation inner, CancellationTokenSource cancellation) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                if (!inner.TryResize(bytes)) return false;
                if (bytes > 128 * 1024) cancellation.Cancel();
                return true;
            }
            public void Dispose() => inner.Dispose();
        }
    }
}
