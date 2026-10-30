using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_CacheOwnsKeysWhenTheProviderRetainsNoPatternText()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new FixedRegexEngine();
        using var budget = new JqBudget(engine, memory, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        byte[] text = Encoding.UTF8.GetBytes(new string('x', 10000));
        using var patternLease = cache.Rent(text, JqRegexCompileOptions.None);
        var pattern = patternLease.Value;
        Assert.Equal(10128, memory.ReservedBytes);
        using (var same = cache.Rent(text.ToArray(), JqRegexCompileOptions.None)) Assert.Same(pattern, same.Value);
        Assert.Equal(1, engine.Compilations);
        ReadOnlyMemory<byte> subject = "x"u8.ToArray();
        Assert.True(cache.IsMatch(pattern, subject, JqRegexMatchOptions.None));
        cache.Dispose();
        Assert.Equal(10128, memory.ReservedBytes);
        Assert.Equal(0, engine.Disposals);
        patternLease.Dispose();
        Assert.Equal(1, engine.Disposals);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_CacheKeyRefusalPrecedesCopyAndProviderWork()
    {
        var root = new BenchmarkMemoryBudget(10127);
        var memory = new CountingMemoryBudget(root);
        var engine = new FixedRegexEngine();
        using var budget = new JqBudget(engine, memory, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        byte[] pattern = Encoding.UTF8.GetBytes(new string('x', 10000));
        Assert.True(Assert.Throws<JqQuotaException>(() => cache.Rent(pattern, JqRegexCompileOptions.None)).IsMemoryAdmission);
        Assert.Equal(0, engine.Compilations);
        Assert.Equal(1, memory.Counts.Refusals);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }

    // A fixed matcher deliberately retains no input pattern, so the test cannot
    // rely on provider compilation estimates to cover Jq's persistent cache key.
    private sealed class FixedRegexEngine : IJqRegexEngine
    {
        internal int Compilations { get; private set; }
        internal int Disposals { get; private set; }
        internal int PeakLive { get; private set; }

        public bool TryCompile(ReadOnlyMemory<byte> patternUtf8, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
            CancellationToken cancellationToken, [NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilations++;
            PeakLive = Math.Max(PeakLive, Compilations - Disposals);
            regex = new Compiled(this);
            error = null;
            return true;
        }

        private sealed class Compiled(FixedRegexEngine owner) : IJqCompiledRegex
        {
            private bool _disposed;
            public IReadOnlyDictionary<int, string> GroupNames { get; } = new Dictionary<int, string>();
            public bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return input.StartsWith("x"u8);
            }
            public JqRegexMatch Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options,
                CancellationToken cancellationToken)
            {
                if (start != 0 || !IsMatch(input, options, cancellationToken)) return default;
                JqRegexCapture[] captures = [new(true, 0, 1)];
                return new JqRegexMatch(captures, null);
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                owner.Disposals++;
            }
        }
    }
}
