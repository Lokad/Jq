using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_RegexIdlePatternReuseHasBoundedAdmission(bool unique)
    {
        var root = new BenchmarkMemoryBudget(140);
        var counted = new CountingMemoryBudget(root);
        var engine = new FixedRegexEngine();
        using var budget = new JqBudget(engine, counted, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        for (int index = 0; index < 1000; index++)
        {
            using var lease = cache.Rent($"p{(unique ? index : 0):D5}", JqRegexCompileOptions.None);
            Assert.Equal(140, root.ReservedBytes);
        }
        Assert.Equal(unique ? 1000 : 1, engine.Compilations);
        Assert.Equal(engine.Compilations, counted.Counts.Reservations);
        Assert.Equal(0, counted.Counts.Resizes);
        Assert.Equal(1, engine.PeakLive);
        cache.Dispose();
        Assert.Equal(engine.Compilations, engine.Disposals);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_RegexNestedPatternsKeepOnlyBorrowedHandlesAndOneIdleHandle()
    {
        var root = new BenchmarkMemoryBudget(1024);
        var engine = new FixedRegexEngine();
        using var budget = new JqBudget(engine, root, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        using var outer = cache.Rent("outer", JqRegexCompileOptions.None);
        using var subject = cache.EncodeSubject("x", outer.Value);
        var match = cache.Match(outer.Value, subject.Value, 0, JqRegexMatchOptions.None);
        var copy = match;
        for (int index = 0; index < 1000; index++)
        {
            using var inner = cache.Rent($"p{index:D5}", JqRegexCompileOptions.None);
            Assert.True(cache.IsMatch(inner.Value, "x"u8.ToArray(), JqRegexMatchOptions.None));
        }
        using (var same = cache.Rent("outer", JqRegexCompileOptions.None))
            Assert.Same(outer.Value, same.Value);
        Assert.Equal(1001, engine.Compilations);
        Assert.Equal(2, engine.PeakLive);
        outer.Dispose();
        outer.Dispose();
        subject.Dispose();
        cache.Dispose();
        Assert.Equal(1000, engine.Disposals);
        Assert.True(match.Success);
        Assert.Equal(138, root.ReservedBytes);
        match.Dispose();
        copy.Dispose();
        Assert.Equal(1001, engine.Disposals);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_RegexIdleEvictionDoesNotRetryRefusedCompilation()
    {
        var root = new BenchmarkMemoryBudget(140);
        var counted = new CountingMemoryBudget(root);
        var engine = new FixedRegexEngine();
        using var budget = new JqBudget(engine, counted, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        using (var first = cache.Rent("first", JqRegexCompileOptions.None)) { }
        var failure = Assert.Throws<JqQuotaException>(() => cache.Rent(new string('x', 100), JqRegexCompileOptions.None));
        Assert.True(failure.IsMemoryAdmission);
        Assert.Equal(1, counted.Counts.Refusals);
        Assert.Equal(1, engine.Compilations);
        Assert.Equal(1, engine.Disposals);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Jq_RegexLongRawStreamsRetainOnlyTheCurrentPattern(bool unique, bool adapted)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new FixedRegexEngine();
        var host = new MockFileSystem();
        host.AddFile("/records", Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, 4096).Select(i => $"p{(unique ? i : 0):D5}\n"))));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rc",
            ". as $p | \"x\" | test($p) | empty", "/records")));
        Assert.Equal(0, await command.ExecuteAsync(host,
            adapted ? engine : JqUtf8RegexEngine.Instance, root, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        if (adapted)
        {
            Assert.Equal(4096, engine.Compilations);
            Assert.Equal(1, engine.PeakLive);
            Assert.Equal(engine.Compilations, engine.Disposals);
        }
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("sub(\"x\"; (reduce range(1000) as $i (\"\"; ($i | tostring) as $p | (\"x\" | test($p)) | \"ok\")))")]
    [InlineData("gsub(\"x\"; (reduce range(1000) as $i (\"\"; ($i | tostring) as $p | (\"x\" | test($p)) | \"ok\")))")]
    public async Task Jq_RegexNestedReplacementRetiresCompletedPatterns(string expression)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new FixedRegexEngine();
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "\"x\" | " + expression)));
        Assert.Equal(0, await command.ExecuteAsync(host, engine, root, CancellationToken.None));
        Assert.Equal("ok\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, engine.PeakLive);
        Assert.Equal(engine.Compilations, engine.Disposals);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_RegexDistinctStreamCancellationReleasesTheNewHandleAndInput()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        int compilations = 0;
        var engine = new ObservedRegexEngine((stage, _, _) =>
        {
            if (stage == "compiled" && ++compilations == 32) cancellation.Cancel();
        });
        var host = new MockFileSystem();
        host.AddFile("/records", string.Concat(Enumerable.Range(0, 4096).Select(i => $"p{i:D5}\n")));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rc",
            ". as $p | \"x\" | test($p) | empty", "/records")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            command.ExecuteAsync(host, engine, root, cancellation.Token));
        Assert.Equal(32, engine.Created);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }
}
