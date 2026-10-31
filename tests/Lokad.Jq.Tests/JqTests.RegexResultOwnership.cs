using Lokad.Jq.Benchmarking;
using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_ProviderResultOwnerRefundsBeforeItsCompiledHandle()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var engine = new OwnedResultRegexEngine();
        using var budget = new JqBudget(engine, memory, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        using var patternLease = cache.Rent("x"u8.ToArray(), JqRegexCompileOptions.None);
        var pattern = patternLease.Value;
        long compiled = memory.ReservedBytes;
        ReadOnlyMemory<byte> subject = "x"u8.ToArray();
        var match = RegexTestOperations.Match(cache, pattern, subject, 0, JqRegexMatchOptions.None);
        var copy = match;
        Assert.Equal(compiled + 32, memory.ReservedBytes);

        cache.Dispose();
        patternLease.Dispose();
        Assert.Empty(engine.Cleanup);
        Assert.Equal(compiled + 32, memory.ReservedBytes);
        Assert.Equal(new JqRegexCapture(true, 0, 1), match.Value);
        match.Dispose();
        copy.Dispose();
        Assert.Equal(new[] { "result", "compiled" }, engine.Cleanup);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ProviderResultRefundsWhenCancellationFollowsReturn()
    {
        using var cancellation = new CancellationTokenSource();
        var engine = new OwnedResultRegexEngine { AfterMatch = cancellation.Cancel };
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/first", "\"x\"\n");
        host.AddFile("/later", "\"later\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", (try match(\"x\") catch \"caught\"), \"after\"", "/first", "/later")));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            command.ExecuteAsync(host, engine, memory, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(new[] { "result", "compiled" }, engine.Cleanup);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class OwnedResultRegexEngine : IJqRegexEngine
    {
        internal List<string> Cleanup { get; } = [];
        internal Action? AfterMatch { get; init; }

        public bool TryCompile(ReadOnlyMemory<byte> patternUtf8, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
            CancellationToken cancellationToken, [NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (memoryBudget is null) throw new InvalidOperationException("Expected shared admission.");
            if (!memoryBudget.TryReserve(64, out var memory))
                throw new JqRegexResourceException("provider admission refused", true);
            regex = new Compiled(this, memoryBudget, memory);
            error = null;
            return true;
        }

        private sealed class Compiled(OwnedResultRegexEngine engine, IMemoryBudget budget,
            IMemoryReservation memory) : SpanRegexCompatibility
        {
            private bool _disposed;
            public override int CaptureCount => 0;
            public override IReadOnlyList<JqRegexGroupName> GroupNames => [];

            public override bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return input.SequenceEqual("x"u8);
            }

            public override JqRegexMatch Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options,
                CancellationToken cancellationToken)
            {
                if (start != 0 || !IsMatch(input, options, cancellationToken)) return default;
                if (!budget.TryReserve(32, out var resultMemory))
                    throw new JqRegexResourceException("provider result admission refused", true);
                var owner = new ResultOwner(engine, resultMemory);
                try
                {
                    JqRegexCapture[] captures = [new(true, 0, 1)];
                    engine.AfterMatch?.Invoke();
                    return new JqRegexMatch(captures, owner);
                }
                catch { owner.Dispose(); throw; }
            }

            public override void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                engine.Cleanup.Add("compiled");
                memory.Dispose();
            }
        }

        private sealed class ResultOwner(OwnedResultRegexEngine engine, IMemoryReservation memory) : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                engine.Cleanup.Add("result");
                memory.Dispose();
            }
        }
    }
}
