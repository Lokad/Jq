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
        var root = new TestMemoryBudget(1024 * 1024);
        var counted = new Lokad.Jq.Benchmarking.CountingMemoryBudget(root);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        string literal = new('x', length);
        var pattern = regexes.Get(literal, JqRegexCompileOptions.None);
        using var subject = regexes.EncodeSubject(literal);
        MemoryCallCounts before = counted.Counts;
        for (int index = 0; index < 1000; index++)
        {
            using var match = regexes.Match(pattern, subject.Value, 0, JqRegexMatchOptions.None);
            Assert.True(match.Value.Success);
            Assert.Equal(1, match.Value.CaptureSlotCount);
            Assert.Equal(new JqRegexCapture(true, 0, length, 0, length), match.Value.Value);
        }
        Assert.Equal(before, counted.Counts);
        regexes.Dispose();
        subject.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexSubjectGrowthAmortizesCallsAndKeepsItsIdleBound()
    {
        var root = new TestMemoryBudget(16 * 1024);
        var memory = new CountingMemoryBudget(root);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        for (int length = 1; length <= 1000; length++)
        {
            string text = new('a', length);
            using var subject = regexes.EncodeSubject(text);
            Assert.Equal(Encoding.UTF8.GetBytes(text), subject.Value.ToArray());
        }
        Assert.InRange(memory.Counts.Reservations, 1, 2);
        Assert.InRange(memory.Counts.Resizes, 1, 24);
        Assert.InRange(root.ReservedBytes, 1064, 4096);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        regexes.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexSubjectPaddingRefusalDoesNotRetryOrEvictAndRecover()
    {
        var root = new TestMemoryBudget(1000);
        var memory = new CountingMemoryBudget(root);
        Assert.True(root.TryReserve(926, out var pressure));
        using (pressure)
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        using (var regexes = new JqRegexCache(budget))
        {
            using (var first = regexes.EncodeSubject("aaaa"))
                Assert.Equal(994, root.ReservedBytes);
            MemoryCallCounts before = memory.Counts;
            var failure = Assert.Throws<JqQuotaException>(() => regexes.EncodeSubject("aaaaa"));
            Assert.True(failure.IsMemoryAdmission);
            Assert.Equal(before.Resizes + 1, memory.Counts.Resizes);
            Assert.Equal(1, memory.Counts.Refusals);
            Assert.Equal(994, root.PeakBytes);
            Assert.Equal(pressure.Bytes, root.ReservedBytes); // Failed operation cleans up its owner.
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexSubjectIdleBoundaryUsesUtf8CapacityIncludingMetadata()
    {
        var root = new TestMemoryBudget(16 * 1024);
        var memory = new CountingMemoryBudget(root);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        foreach (string text in new[] { new string('a', 4032), new string('é', 2016), string.Concat(Enumerable.Repeat("🚀", 1008)) })
        {
            using var subject = regexes.EncodeSubject(text);
            Assert.Equal(Encoding.UTF8.GetBytes(text), subject.Value.ToArray());
            Assert.Equal(4096, root.ReservedBytes);
        }
        Assert.Equal(1, memory.Counts.Reservations);
        Assert.Equal(0, memory.Counts.Resizes);
        using (var large = regexes.EncodeSubject(new string('é', 2016) + "a"))
            Assert.Equal(4033, large.Value.Length);
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(8129, root.PeakBytes);
    }

    [Fact]
    public void Jq_GovernedRegexCacheRetainsPatternsAndRollsBackFailedCompilation()
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        using var scope = memory.CreateScope();
        var budget = new JqBudget(JqUtf8RegexEngine.Instance, scope, CancellationToken.None);
        using (var regexes = new JqRegexCache(budget))
        {
            var pattern = regexes.Get("x", JqRegexCompileOptions.None);
            Assert.Equal(130, memory.ReservedBytes);
            Assert.Same(pattern, regexes.Get("x", JqRegexCompileOptions.None));
            Assert.Equal(130, memory.ReservedBytes);
            Assert.StartsWith("invalid regex:", Assert.Throws<JqException>(() =>
                regexes.Get("[", JqRegexCompileOptions.None)).Message, StringComparison.Ordinal);
            Assert.Equal(130, memory.ReservedBytes);
            Assert.Equal(260, memory.PeakBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexKeepsBoundedSubjectAndRefundsFailedEncoding()
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        using var scope = memory.CreateScope();
        var budget = new JqBudget(JqUtf8RegexEngine.Instance, scope, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        var pattern = regexes.Get("x", JqRegexCompileOptions.None);
        long retained = memory.ReservedBytes;
        using (var subject = regexes.EncodeSubject("é🚀"))
        {
            Assert.Equal(retained + 70, memory.ReservedBytes);
            using (var match = regexes.Match(pattern, subject.Value, 0, JqRegexMatchOptions.None))
            {
                Assert.False(match.Value.Success);
                Assert.Equal(retained + 70, memory.ReservedBytes);
            }
            Assert.Equal(retained + 70, memory.ReservedBytes);
        }
        Assert.Equal(retained + 70, memory.ReservedBytes);
        Assert.Throws<JqException>(() => regexes.EncodeSubject("a\ud800"));
        Assert.Equal(retained, memory.ReservedBytes);
        regexes.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexNestedLeasesAndCopiedResultsSurviveCacheDisposal()
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        var pattern = regexes.Get("x", JqRegexCompileOptions.None);
        long compiled = memory.ReservedBytes;
        using var parent = regexes.EncodeSubject("x");
        using var nested = regexes.EncodeSubject("yy");
        Assert.Equal("x"u8.ToArray(), parent.Value.ToArray());
        Assert.Equal(compiled + 65 + 66, memory.ReservedBytes);
        var first = regexes.Match(pattern, parent.Value, 0, JqRegexMatchOptions.None);
        var stale = first;
        first.Dispose();
        var current = regexes.Match(pattern, parent.Value, 0, JqRegexMatchOptions.None);
        Assert.True(current.Value.Success);
        stale.Dispose(); // A copied old result must not release the new lease.
        nested.Dispose();
        regexes.Dispose();
        parent.Dispose();
        parent.Dispose();
        Assert.Equal(compiled, memory.ReservedBytes);
        Assert.Throws<ObjectDisposedException>(() => regexes.EncodeSubject("x"));
        current.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
        current.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexSubjectReuseIgnoresOldDisposeAndDropsLargeOwners()
    {
        var memory = new TestMemoryBudget(2 * 1024 * 1024);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        var old = regexes.EncodeSubject("x");
        old.Dispose();
        using var current = regexes.EncodeSubject("y");
        old.Dispose();
        Assert.Equal(65, memory.ReservedBytes);
        using (var large = regexes.EncodeSubject(new string('a', 5000)))
            Assert.Equal(65 + 5064, memory.ReservedBytes);
        Assert.Equal(65, memory.ReservedBytes);
        var pattern = regexes.Get(new string('x', 100), JqRegexCompileOptions.None);
        long retained = memory.ReservedBytes;
        using (var match = regexes.Match(pattern, current.Value, 0, JqRegexMatchOptions.None))
        {
            Assert.False(match.Value.Success);
            Assert.Equal(retained, memory.ReservedBytes);
        }
        Assert.Equal(retained, memory.ReservedBytes);
        regexes.Dispose();
        Assert.Equal(retained, memory.ReservedBytes);
        current.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedRegexRefusedSubjectGrowthRefundsItsIdleOwner()
    {
        var memory = new TestMemoryBudget(400);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None);
        using var regexes = new JqRegexCache(budget);
        regexes.Get("x", JqRegexCompileOptions.None);
        using (var subject = regexes.EncodeSubject("x"))
            Assert.Equal(130 + 65, memory.ReservedBytes);
        Assert.Throws<JqQuotaException>(() => regexes.EncodeSubject(new string('a', 400)));
        Assert.Equal(130, memory.ReservedBytes);
        using (var next = regexes.EncodeSubject("y"))
            Assert.Equal("y"u8.ToArray(), next.Value.ToArray());
        regexes.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedRegexRepeatsWithBoundedCacheAndCompleteExecutionRefunds()
    {
        var memory = new TestMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "range(1000) | \"x\" | test(\"x\")")));
        for (int repeat = 0; repeat < 2; repeat++)
        {
            var host = new MockFileSystem();
            Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Equal(string.Concat(Enumerable.Repeat("true\n", 1000)), host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, memory.ReservedBytes);
        }
        Assert.InRange(memory.PeakBytes, 1, 64 * 1024);
    }

    [Fact]
    public async Task Jq_GovernedNestedRegexReplacementKeepsParentAdmissionsLive()
    {
        var memory = new TestMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("\"ab\"\n", 2000)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "gsub(\"(?<x>.)\"; .x | gsub(\"(?<x>.)\"; \"y\"))")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("yy\n", 2000)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.True(memory.PeakBytes < 64 * 1024);
    }
}
