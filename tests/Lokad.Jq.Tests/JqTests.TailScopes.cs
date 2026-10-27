using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("def f($n): if $n==0 then error(\"x\") else try f($n-1) catch $n end; f(2)", "1\n")]
    [InlineData("def f($n): if $n==0 then error(\"x\") else f($n-1)? end; f(2)", "")]
    [InlineData("def f($n): if $n==0 then . else try f(error(\"arg\")) catch \"caught\" end; f(1)", "\"caught\"\n")]
    [InlineData("def f($n): if $n==0 then . else try f((0,error(\"later\"))) catch \"caught\" end; f(1)", "null\n\"caught\"\n")]
    [InlineData("def f($n): if $n==0 then error(\"x\") else try f($n-1) catch $n end; try f(2) catch \"outer\"", "1\n")]
    [InlineData("def f(g;$n): if $n==0 then g else label $out | f(break $out;$n-1) end; f(.;1)", "")]
    [InlineData("def f($n): if $n==0 then 0 else label $out | f($n-1) end; f(3)", "0\n")]
    [InlineData("def f($n): if $n==0 then error(\"x\") else try f($n-1) catch if $n==1 then error(\"again\") else $n end end; f(2)", "2\n")]
    [InlineData("def f($n): if $n==0 then . else try error(\"trigger\") catch f($n-1) end; f(3)", "\"trigger\"\n")]
    [InlineData("def f($n): if $n==0 then error(.) else try f($n-1) catch . end; {a:1} | path(f(1))", "[]\n")]
    [InlineData("def f($n): if $n==0 then .a else f($n-1)? end; {a:1} | path(f(2))", "[\"a\"]\n")]
    public async Task Jq_RecursiveCallsRetainTheirControlScopes(string source, string expected)
    {
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try f($n-1) catch 99")]
    [InlineData("f($n-1)?")]
    [InlineData("label $out | f($n-1)")]
    public async Task Jq_RecursionWithLiveScopesKeepsTerminalDepthProtection(string step)
    {
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "null\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "\"ready\", (def f($n): if $n==0 then 0 else " + step + " end; try f(5000) catch 42)", "/input")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: filter nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ActiveRecursiveHandlersPropagateCancellationAndCloseInput()
    {
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.AddFile("/input", "null\n");
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 2) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "def spin: .,spin; def f($n): if $n==0 then spin else try f($n-1) catch 42 end; f(3)", "/input")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token));
        Assert.Equal("null\nnull\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ActiveRecursiveHandlersCannotCatchAdmissionRefusal()
    {
        const long allowance = 128 * 1024;
        var root = new BenchmarkMemoryBudget(allowance);
        IMemoryReservation? pressure = null;
        var host = new MockFileSystem();
        host.BeforeByteAppend = () =>
        {
            if (host.AppendCallCount == 0)
                Assert.True(root.TryReserve(allowance - root.ReservedBytes - 1024, out pressure));
        };
        try
        {
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
                "def f($n): if $n==0 then (\"ready\", \"x\"*10000) else try f($n-1) catch \"caught\" end; f(3)")));
            Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
            Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(Assert.IsAssignableFrom<IMemoryReservation>(pressure).Bytes, root.ReservedBytes);
        }
        finally { pressure?.Dispose(); }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_DownstreamClosureAbandonsDeferredRecursiveArgumentFailures()
    {
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = false };
        host.AddFile("/input", "null\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "def f($n): if $n==0 then . else try f((0,error(\"late\"))) catch 42 end; f(1)", "/input")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("null\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
