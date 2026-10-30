using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("1|path(.foo?)", "", "")]
    [InlineData("try (1|path(.foo)) catch .", "\"Cannot index number with string (\\\"foo\\\")\"\n", "")]
    [InlineData("try path(({}).foo?) catch .", "\"Invalid path expression near attempt to access element \\\"foo\\\" of {}\"\n", "")]
    [InlineData("try path((1+1).foo?) catch .", "\"Invalid path expression near attempt to access element \\\"foo\\\" of 2\"\n", "")]
    [InlineData("try path((error(\"source\")|.foo?)) catch .", "\"source\"\n", "")]
    [InlineData("try path(({},1).foo?) catch .", "\"Invalid path expression near attempt to access element \\\"foo\\\" of {}\"\n", "")]
    [InlineData("try path(([],null).foo?) catch .", "\"Invalid path expression near attempt to access element \\\"foo\\\" of []\"\n", "")]
    [InlineData("try path((debug|.foo?)) catch .", "[\"foo\"]\n", "[\"DEBUG:\",null]\n")]
    [InlineData("try path(.foo? | error(\"later\")) catch .", "\"later\"\n", "")]
    [InlineData("try path(({}.foo?).bar) catch .", "\"Invalid path expression near attempt to access element \\\"foo\\\" of {}\"\n", "")]
    [InlineData("try (1 as {(\"a\\nb\"):$x}|$x) catch .", "\"Cannot index number with string (\\\"a\\\\nb\\\")\"\n", "")]
    [InlineData("try ({} as [$x]|$x) catch .", "\"Cannot index object with number (0)\"\n", "")]
    [InlineData("try (1 as {foo:$x}|$x) catch .", "\"Cannot index number with string (\\\"foo\\\")\"\n", "")]
    [InlineData("{foo:[1]} as {$foo}|$foo|path($foo)", "[]\n", "")]
    [InlineData("try (1 as {((debug|\"foo\")):$x}|$x) catch .", "\"Cannot index number with string (\\\"foo\\\")\"\n", "[\"DEBUG:\",1]\n")]
    public async Task Jq_SharedReadsPreserveNativeDiagnosticsAndOptionalPathBoundaries(string filter, string output, string diagnostic)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("null");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(1048576)]
    public async Task Jq_SharedReadsClipBorrowedPatternKeysBeforeConstructingDiagnostics(int length)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024 + 80 + length);
        var host = new MockFileSystem();
        string key = new('z', length);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "key", key,
            "\"ready\", (try (1 as {($key):$x}|$x) catch .)")));
        long before = 0;
        host.BeforeByteAppend = () =>
        {
            if (host.AppendCallCount == 0) before = GC.GetAllocatedBytesForCurrentThread();
        };
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256 * 1024);
        Assert.Equal("ready\nCannot index number with string (\"" + new string('z', 24) + "...\")\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_SharedReadsKeepDiagnosticRefusalTerminalEvenWithCancellation(bool cancel)
    {
        string key = new('z', 1048576);
        var memory = new BenchmarkMemoryBudget(0);
        using var cancellation = new CancellationTokenSource();
        IMemoryBudget governed = cancel ? new AdmissionCallbackBudget(memory, cancellation.Cancel) : memory;
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, governed, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<JqQuotaException>(() => JqPathReads.ReadField(NativeFixtures.Create(1), key, runtime, budget));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_SharedReadsObserveCancellationAfterDiagnosticAdmission()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var governed = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, governed, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            Assert.ThrowsAny<OperationCanceledException>(() => JqPathReads.ReadField(NativeFixtures.Create(1), "foo", runtime, budget));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }
}
