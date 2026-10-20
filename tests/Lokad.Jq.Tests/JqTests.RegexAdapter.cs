using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("compile", "test(\"x\")", 1)]
    [InlineData("compiled", "test(\"x\")", 1)]
    [InlineData("boolean", "test(\"x\")", 1)]
    [InlineData("boolean", "all(range(1000); \"x\" | test(\"x\"))", 1)]
    [InlineData("boolean", "any(range(1000); \"x\" | test(\"x\"))", 1)]
    [InlineData("boolean", "reduce range(1000) as $_ (\"\"; \"x\" | test(\"x\") | \"x\" * 128)", 1)]
    [InlineData("detailed", "match(\"x\")", 1)]
    [InlineData("detailed", "match(\"x\"; \"g\")", 2)]
    [InlineData("detailed", "gsub(\"x\"; \"y\")", 2)]
    [InlineData("detailed", "gsub(\"x\"; \"x\" | gsub(\"x\"; \"y\"))", 2)]
    [InlineData("detailed", "sub(\"x\"; \"y\")", 1)]
    public async Task Jq_AdapterCancellationDuringWorkUnwindsOwnedInputs(string stage, string expression, int call)
    {
        using var cancellation = new CancellationTokenSource();
        var memory = new TestMemoryBudget(1024 * 1024);
        int calls = 0;
        var engine = new ObservedRegexEngine((operation, budget, token) =>
        {
            Assert.NotNull(budget);
            Assert.Equal(cancellation.Token, token);
            Assert.True(memory.ReservedBytes > 0);
            if (operation == stage && ++calls == call) cancellation.Cancel();
            if (operation != "compiled") token.ThrowIfCancellationRequested();
        });
        var regexEngine = engine;
        var host = new MockFileSystem();
        host.AddFile("/first", "\"xx\"\n\"later\"\n");
        host.AddFile("/later", "\"later\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", (try (" + expression + ") catch \"caught\"), \"after\"", "/first", "/later")));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            command.ExecuteAsync(host, regexEngine, memory, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(call, calls);
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.True(memory.TryReserve(1024 * 1024, out var reusable));
        reusable.Dispose();
    }

    [Theory]
    [InlineData("compile", "test(\"x\")")]
    [InlineData("boolean", "test(\"x\")")]
    [InlineData("detailed", "match(\"x\")")]
    public async Task Jq_AdapterRefusalIsTerminalAndNeverRetried(string stage, string expression)
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        int refusals = 0;
        var engine = new ObservedRegexEngine((operation, budget, token) =>
        {
            if (operation != stage) return;
            Assert.NotNull(budget);
            Assert.False(budget.TryReserve(1024 * 1024, out var refused));
            Assert.Null(refused);
            refusals++;
            throw new JqRegexResourceException("provider admission refused", true);
        });
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "\"prefix\", (\"x\" | (try " + expression + " catch \"caught\")?), \"later\"")));

        Assert.Equal(5, await command.ExecuteAsync(host, engine, memory, CancellationToken.None));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, refusals);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_AdapterHandleWaitsForLastBorrowerAndIsDisposedOnce()
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        var engine = new ObservedRegexEngine((_, _, _) => { });
        using var budget = new JqBudget(engine, memory, CancellationToken.None);
        using var cache = new JqRegexCache(budget);
        var pattern = cache.Get("x", JqRegexCompileOptions.None);
        using var subject = cache.EncodeSubject("x");
        var match = cache.Match(pattern, subject.Value, 0, JqRegexMatchOptions.None);
        cache.Dispose();
        subject.Dispose();
        Assert.Equal(0, engine.Disposed);
        Assert.True(memory.ReservedBytes > 0);
        Assert.True(match.Value.Success);
        match.Dispose();
        match.Dispose();
        Assert.Equal(1, engine.Created);
        Assert.Equal(1, engine.Disposed);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class ObservedRegexEngine(Action<string, IMemoryBudget?, CancellationToken> observe) : IJqRegexEngine
    {
        internal int Created { get; private set; }
        internal int Disposed { get; private set; }

        public bool TryCompile(string pattern, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
            CancellationToken cancellationToken, [NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error)
        {
            observe("compile", memoryBudget, cancellationToken);
            if (!JqUtf8RegexEngine.Instance.TryCompile(pattern, options, memoryBudget, cancellationToken, out var compiled, out error))
            { regex = null; return false; }
            Created++;
            regex = new ObservedCompiled(compiled, memoryBudget, this, observe);
            observe("compiled", memoryBudget, cancellationToken);
            return true;
        }

        private sealed class ObservedCompiled(IJqCompiledRegex inner, IMemoryBudget? budget,
            ObservedRegexEngine owner, Action<string, IMemoryBudget?, CancellationToken> observe) : IJqCompiledRegex
        {
            private bool _disposed;
            public IReadOnlyDictionary<int, string> GroupNames => inner.GroupNames;
            public bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken)
            {
                observe("boolean", budget, cancellationToken);
                return inner.IsMatch(input, options, cancellationToken);
            }
            public JqRegexMatch Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options,
                CancellationToken cancellationToken)
            {
                observe("detailed", budget, cancellationToken);
                return inner.Match(input, start, options, cancellationToken);
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                inner.Dispose();
                owner.Disposed++;
            }
        }
    }
}
