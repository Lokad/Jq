using Lokad.Jq.Benchmarking;
using System.Text;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(17000)]
    public void Jq_LiteralMatchesUseProviderCaptureStorage(int length)
    {
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var counted = new Lokad.Jq.Benchmarking.CountingMemoryBudget(root);
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        string literal = new('x', length);
        using var patternLease = regexes.Rent(Encoding.UTF8.GetBytes(literal), JqRegexCompileOptions.None);
        var pattern = patternLease.Value;
        ReadOnlyMemory<byte> subject = Encoding.UTF8.GetBytes(literal);
        MemoryCallCounts before = counted.Counts;
        for (int index = 0; index < 1000; index++)
        {
            using var match = RegexTestOperations.Match(regexes, pattern, subject, 0, JqRegexMatchOptions.None);
            Assert.True(match.Success);
            Assert.Equal(1, match.CaptureSlotCount);
            Assert.Equal(new JqRegexCapture(true, 0, length), match.Value);
        }
        Assert.Equal(before, counted.Counts);
        regexes.Dispose();
        patternLease.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexCacheRetainsPatternsAndRollsBackFailedCompilation()
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        using var scope = memory.CreateScope();
        var budget = new JqBudget(Utf8RegexAdapter.Instance, scope, CancellationToken.None);
        using (var regexes = new JqRegexCache(budget))
        {
            using var patternLease = regexes.Rent("x"u8.ToArray(), JqRegexCompileOptions.None);
            var pattern = patternLease.Value;
            Assert.Equal(129, memory.ReservedBytes);
            using (var same = regexes.Rent("x"u8.ToArray(), JqRegexCompileOptions.None)) Assert.Same(pattern, same.Value);
            Assert.Equal(129, memory.ReservedBytes);
            Assert.StartsWith("invalid regex:", Assert.Throws<JqException>(() =>
                regexes.Rent("["u8.ToArray(), JqRegexCompileOptions.None)).Message, StringComparison.Ordinal);
            Assert.Equal(129, memory.ReservedBytes);
            Assert.Equal(258, memory.PeakBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexNestedLeasesAndCopiedResultsSurviveCacheDisposal()
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        using var patternLease = regexes.Rent("x"u8.ToArray(), JqRegexCompileOptions.None);
        var pattern = patternLease.Value;
        long compiled = memory.ReservedBytes;
        ReadOnlyMemory<byte> parent = "x"u8.ToArray();
        Assert.Equal(compiled, memory.ReservedBytes);
        var first = RegexTestOperations.Match(regexes, pattern, parent, 0, JqRegexMatchOptions.None);
        var stale = first;
        first.Dispose();
        var current = RegexTestOperations.Match(regexes, pattern, parent, 0, JqRegexMatchOptions.None);
        Assert.True(current.Value.Success);
        stale.Dispose(); // A copied old result must not release the new lease.
        regexes.Dispose();
        patternLease.Dispose();
        Assert.Equal(compiled, memory.ReservedBytes);
        Assert.Throws<ObjectDisposedException>(() => RegexTestOperations.Match(regexes, pattern, parent, 0, JqRegexMatchOptions.None));
        current.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
        current.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedRegexRepeatsWithBoundedCacheAndCompleteExecutionRefunds()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "range(1000) | \"x\" | test(\"x\")")));
        for (int repeat = 0; repeat < 2; repeat++)
        {
            var host = new MockFileSystem();
            Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
            Assert.Equal(string.Concat(Enumerable.Repeat("true\n", 1000)), host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, memory.ReservedBytes);
        }
        Assert.InRange(memory.PeakBytes, 1, 64 * 1024);
    }

    [Fact]
    public async Task Jq_GovernedNestedRegexReplacementKeepsParentAdmissionsLive()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("\"ab\"\n", 2000)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "gsub(\"(?<x>.)\"; .x | gsub(\"(?<x>.)\"; \"y\"))")));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("yy\n", 2000)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.True(memory.PeakBytes < 64 * 1024);
    }
}
