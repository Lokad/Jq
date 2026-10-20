using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("try first(recurse(if . == 0 then (1,error(\"late\")) else empty end) | select(. == 1)) catch .", "1\n", "")]
    [InlineData("try first(while(. < 2; (.+1,error(\"late\"))) | select(. == 1)) catch .", "1\n", "")]
    [InlineData("try first(until(. == 1; (1,error(\"late\")))) catch .", "1\n", "")]
    [InlineData("[limit(3; recurse(.+1; (false,true)))]", "[0,1,2]\n", "")]
    [InlineData("[recurse(if . == 0 then 1 else empty end; (true,true))]", "[0,1,1]\n", "")]
    [InlineData("[limit(4; while((true,true); .+1))]", "[0,1,2,3]\n", "")]
    [InlineData("[limit(3; recurse((.+1,.+2)|debug))]", "[0,1,2]\n", "[\"DEBUG:\",1]\n[\"DEBUG:\",2]\n")]
    [InlineData("[while(.<2; (.+1,.+2)|debug)]", "[0,1]\n", "[\"DEBUG:\",1]\n[\"DEBUG:\",2]\n[\"DEBUG:\",3]\n[\"DEBUG:\",2]\n")]
    [InlineData("[until(.>=2; (.+1,.+2)|debug)]", "[2,3,2]\n", "[\"DEBUG:\",1]\n[\"DEBUG:\",2]\n[\"DEBUG:\",3]\n[\"DEBUG:\",2]\n")]
    public async Task Jq_TraversalKeepsLazyErrorsConditionsAndEffects(string source, string expected, string error)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "0 | " + source)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("last(limit(100000; while(true; .+1)))", "99999\n")]
    [InlineData("until(. == 100000; .+1)", "100000\n")]
    [InlineData("last(limit(100000; recurse(.+1)))", "99999\n")]
    public async Task Jq_SingleValuedTraversalTailsKeepLongLoopsBounded(string source, string expected)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "0 | " + source)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("def inc: .+1; 0 | until(. == 100000; inc)", "100000\n")]
    [InlineData("def inc($n): .+$n; 0 | until(. == 100000; inc(1))", "100000\n")]
    [InlineData("0 | last(limit(100000; recurse(.+1 | floor)))", "99999\n")]
    [InlineData("0 | last(limit(100000; while(isfinite; .+1)))", "99999\n")]
    [InlineData("0 | until(. == 100000; if .<100000 then .+1 else . end)", "100000\n")]
    [InlineData("def f: f; 0 | first(recurse(f))", "0\n")]
    public async Task Jq_ProvenBuiltinAndUserFunctionLoopTailsStayBounded(string source, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_LoopProofDoesNotExpandARepeatedFunctionCallGraph()
    {
        string definitions = "def f0: .; " + string.Concat(Enumerable.Range(1, 24)
            .Select(index => $"def f{index}: f{index - 1} + f{index - 1}; "));
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", definitions + "0 | first(recurse(f24))")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("first(recurse(empty))")]
    [InlineData("first(while(true;empty))")]
    [InlineData("until(true;empty)")]
    public async Task Jq_TraversalBorrowsStateAliases(string source)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1,2]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source + " as $x | path($x)")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Jq_TraversalPullsOneInfiniteSiblingAndDisposesItsEnumerator(int kind)
    {
        var source = new TraversalSourceFilter(static () => { });
        JqFilter filter = kind == 0 ? new RecurseFilter(source, null)
            : kind == 1 ? new WhileFilter(new LiteralFilter(JsonValue.Create(true)), source)
            : new UntilFilter(new TraversalNullFilter(), source);
        var root = new BenchmarkMemoryBudget(16 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            Assert.Equal(kind == 2 ? 1 : 2, filter.Evaluate(kind == 2 ? JsonValue.Create(0) : null, context, context.RootEnvironment)
                .Take(kind == 2 ? 1 : 2).Count());
            Assert.Equal(1, source.Pulls);
            Assert.Equal(1, source.Disposals);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_TraversalCancelsWhileRejectingInfiniteChildrenAndDisposesThem()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var source = new TraversalSourceFilter(() => { if (++calls == 10) cancellation.Cancel(); });
        var root = new BenchmarkMemoryBudget(16 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token)))
        {
            var filter = new RecurseFilter(source, new LiteralFilter(JsonValue.Create(false)));
            using var outputs = filter.Evaluate(null, context, context.RootEnvironment).GetEnumerator();
            Assert.True(outputs.MoveNext());
            Assert.Throws<OperationCanceledException>(() => outputs.MoveNext());
            Assert.Equal(10, calls);
            Assert.Equal(1, source.Disposals);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("limit(3; recurse((.+1,.+2)))", "ready\n0\n")]
    [InlineData("limit(3; while(true; (.+1,.+2)))", "ready\n0\n")]
    [InlineData("until(.>2; (.+1,.+2))", "ready\n")]
    public async Task Jq_TraversalPreflightsRetainedContinuationsAndFailsOutsideHandlers(string source, string expected)
    {
        foreach (bool optional in new[] { false, true })
        {
            const long limit = 4 * 1024 * 1024;
            var root = new BenchmarkMemoryBudget(limit);
            var host = new MockFileSystem();
            host.AddFile("/input", "0");
            string operation = "0 | " + source;
            string guarded = optional ? "(" + operation + ")?" : "try (" + operation + ") catch \"caught\"";
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (" + guarded + ")", "/input")));
            IMemoryReservation? pressure = null;
            host.BeforeByteAppend = () =>
            {
                if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes - 1024, out pressure));
            };
            Assert.Equal(5, await command.ExecuteAsync(host, root, CancellationToken.None));
            Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Single(host.ClosedDescriptors);
            Assert.Equal(0, host.OpenFileCount);
            Assert.NotNull(pressure);
            Assert.Equal(pressure.Bytes, root.ReservedBytes);
            pressure.Dispose();
            Assert.Equal(0, root.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("while(true; .+1)")]
    [InlineData("recurse(.+1)")]
    [InlineData("until(.>0; .+1)")]
    public async Task Jq_TraversalStopsOnDownstreamClosureAndClosesInput(string source)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = false };
        host.AddFile("/input", "0");
        host.AddFile("/later", "0");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source, "/input", "/later")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(source.StartsWith("until", StringComparison.Ordinal) ? "1\n" : "0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(1, host.AppendCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("recurse((.+1,.+2)) | select(.>0)", "1\n", false)]
    [InlineData("recurse((.+1,.+2)) | select(.>0)", "1\n", true)]
    [InlineData("while(.<3; (.+1,.+2)) | select(.>0)", "1\n", false)]
    [InlineData("while(.<3; (.+1,.+2)) | select(.>0)", "1\n", true)]
    [InlineData("until(.>1; (.+1,.+2))", "2\n", false)]
    [InlineData("until(.>1; (.+1,.+2))", "2\n", true)]
    public async Task Jq_TraversalKeepsPendingOutputAndCleanupAdmitted(string source, string expected, bool cancel)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem { AppendRemainsOpen = false };
        inner.AddFile("/input", "0");
        var host = new MemoryBorrowHost(inner) { DelayCleanup = true };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source, "/input")));
        Task<int> execution = command.ExecuteAsync(host, root, cancellation.Token);
        ReadOnlyMemory<byte> pending = await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        byte[] first = System.Text.Encoding.UTF8.GetBytes(expected);
        long charged = root.ReservedBytes;
        Assert.Equal(first, pending.ToArray());
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, root.ReservedBytes);
        Assert.Equal(first, pending.ToArray());
        host.Release.SetResult();
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(execution.IsCompleted);
        Assert.True(root.ReservedBytes > 0);
        host.CloseRelease.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else
        {
            Assert.Equal(0, await execution);
            Assert.Equal(expected, inner.GetOutput(JqFileDescriptor.StdOut));
        }
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    private sealed class TraversalNullFilter : JqFilter
    {
        protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
        {
            yield return JsonValue.Create(input is null);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Jq_TraversalCleanupPreservesTerminalFailures(bool primaryTerminal, bool cancellation)
    {
        Exception terminal = cancellation ? new OperationCanceledException()
            : new JqQuotaException("memory budget exceeded") { IsMemoryAdmission = true };
        var language = new JqException("language");
        var body = new TraversalFailureFilter(primaryTerminal ? terminal : language, primaryTerminal ? language : terminal);
        var root = new BenchmarkMemoryBudget(16 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            var filter = new RecurseFilter(body, null);
            Exception? thrown = Record.Exception(() => filter.Evaluate(JsonValue.Create(0), context, context.RootEnvironment).ToList());
            Assert.Same(terminal, thrown);
            Assert.Equal(1, body.Disposals);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    private sealed class TraversalFailureFilter(Exception primary, Exception cleanup) : JqFilter
    {
        public int Disposals { get; private set; }
        protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
        {
            if (input is null) throw primary;
            try { yield return null; }
            finally
            {
                Disposals++;
                throw cleanup;
            }
        }
    }

    private sealed class TraversalSourceFilter(Action onPull) : JqFilter
    {
        public int Pulls { get; private set; }
        public int Disposals { get; private set; }
        protected override IEnumerable<JsonNode?> EvaluateCore(JsonNode? input, JqContext context, JqEnvironment environment)
        {
            try
            {
                while (true)
                {
                    Pulls++;
                    onPull();
                    yield return null;
                }
            }
            finally { Disposals++; }
        }
    }
}
