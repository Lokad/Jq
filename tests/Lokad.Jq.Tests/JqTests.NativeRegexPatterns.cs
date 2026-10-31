using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("")]
    [InlineData("ordinary")]
    [InlineData("é\0😀e\u0301")]
    public void Jq_RegexCacheOwnsNativePatternBytesThroughItsBorrowers(string text)
    {
        byte[] input = Encoding.UTF8.GetBytes(text);
        byte[] original = input.ToArray();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new BorrowedPatternRegexEngine();
        using var budget = new JqBudget(engine, memory, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        using var first = cache.Rent(input, JqRegexCompileOptions.None);
        Array.Fill(input, (byte)'z');
        using (var same = cache.Rent(original, JqRegexCompileOptions.None))
            Assert.Same(first.Value, same.Value);
        using (var differentOptions = cache.Rent(original, JqRegexCompileOptions.Caseless))
            Assert.NotSame(first.Value, differentOptions.Value);
        Assert.Equal(2, engine.Compilations);
        Assert.True(RegexTestOperations.IsMatch(cache, first.Value, original, JqRegexMatchOptions.None));
        cache.Dispose();
        Assert.Equal(1, engine.Disposals);
        Assert.True(memory.ReservedBytes > 0);
        first.Dispose();
        first.Dispose();
        Assert.Equal(2, engine.Disposals);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_LargeNativeRegexPatternNeedsOnlyItsOwnedByteKey()
    {
        const int length = 1024 * 1024;
        const long limit = 4 * length;
        var memory = new BenchmarkMemoryBudget(limit);
        var engine = new FixedRegexEngine();
        var host = new MockFileSystem();
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null)
                Assert.True(memory.TryReserve(limit - memory.ReservedBytes - length - 8192, out pressure));
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "pattern", new string('x', length),
            "\"ready\", (\"x\" | test($pattern)), (\"x\" | test($pattern))")));
        Assert.Equal(0, await command.ExecuteAsync(host, engine, memory, CancellationToken.None));
        Assert.Equal("ready\ntrue\ntrue\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, engine.Compilations);
        Assert.Equal(1, engine.Disposals);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_NativeRegexKeyCopyChecksCancellationAfterAdmission()
    {
        byte[] pattern = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024));
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        var engine = new FixedRegexEngine();
        using var budget = new JqBudget(engine, hooked, cancellation.Token);
        using var cache = new JqRegexCache(budget);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<OperationCanceledException>(() => cache.Rent(pattern, JqRegexCompileOptions.None));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        Assert.Equal(0, engine.Compilations);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class BorrowedPatternRegexEngine : IJqRegexEngine
    {
        internal int Compilations { get; private set; }
        internal int Disposals { get; private set; }

        public bool TryCompile(ReadOnlyMemory<byte> patternUtf8, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
            CancellationToken cancellationToken, [NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilations++;
            regex = new Compiled(this, patternUtf8);
            error = null;
            return true;
        }

        private sealed class Compiled(BorrowedPatternRegexEngine owner, ReadOnlyMemory<byte> pattern) : SpanRegexCompatibility
        {
            private bool _disposed;
            public override int CaptureCount => 0;
            public override IReadOnlyList<JqRegexGroupName> GroupNames => [];
            public override bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                return input.SequenceEqual(pattern.Span);
            }
            public override JqRegexMatch Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options,
                CancellationToken cancellationToken) => throw new NotSupportedException();
            public override void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                owner.Disposals++;
            }
        }
    }
}
