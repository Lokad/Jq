using Lokad.Jq.Benchmarking;
using System.Globalization;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("json", 64, 0)]
    [InlineData("json", 65, 5)]
    [InlineData("seq", 64, 0)]
    [InlineData("seq", 65, 5)]
    [InlineData("stream", 64, 0)]
    [InlineData("stream", 65, 5)]
    [InlineData("stream-errors", 64, 0)]
    [InlineData("stream-errors", 65, 5)]
    [InlineData("seq-stream", 64, 0)]
    [InlineData("seq-stream", 65, 5)]
    public async Task Jq_InputDepthUsesTheSharedBoundary(string mode, int depth, int status)
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        var arguments = new List<string>();
        if (mode.Contains("seq", StringComparison.Ordinal)) arguments.Add("--seq");
        if (mode == "stream-errors") arguments.Add("--stream-errors");
        else if (mode.Contains("stream", StringComparison.Ordinal)) arguments.Add("--stream");
        arguments.AddRange(["empty", "/input"]);
        host.AddFile("/input", (mode.Contains("seq", StringComparison.Ordinal) ? "\u001e" : "")
            + new string('[', depth) + "0" + new string(']', depth));
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, []);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(status, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(status == 0 ? "" : "jq: value nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public async Task Jq_ValueConsumersUseTheSharedDepth(int depth)
    {
        string path = "null | setpath([range(" + depth.ToString(CultureInfo.InvariantCulture) + ") | \"a\"]; 0)";
        foreach (var (operation, expected) in new[]
        {
            (". == (" + path + ")", "true\n"),
            (". < (" + path + ")", "false\n"),
            ("contains(" + path + ")", "true\n"),
            (". * . | length", "1\n"),
            ("tojson | length", (6 * depth + 1).ToString(CultureInfo.InvariantCulture) + "\n"),
        })
        {
            var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
            var host = new MockFileSystem();
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", path + " | " + operation)));

            Assert.Equal(depth == 64 ? 0 : 5, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Equal(depth == 64 ? expected : "", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(depth == 64 ? "" : "jq: value nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(64, 0)]
    [InlineData(65, 5)]
    public async Task Jq_ModuleLoadingUsesTheSharedDepth(int count, int status)
    {
        var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        var host = new MockFileSystem();
        for (int index = 0; index < count; index++)
        {
            string next = (index + 1).ToString(CultureInfo.InvariantCulture);
            host.AddFile("/m" + index.ToString(CultureInfo.InvariantCulture) + ".jq",
                (index + 1 < count ? "import \"m" + next + "\" as next; " : "") + "def f: 0;");
        }
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-L", "/", "import \"m0\" as m; empty")));

        Assert.Equal(status, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(status == 0 ? "" : "jq: module nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(Math.Min(count, 64), host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void Jq_RecursiveEnumerationRestoresDepthAfterCompletionOrFailure(int depth)
    {
        using var budget = new JqBudget(CancellationToken.None);
        using (IEnumerator<int> values = Enumerate(depth).GetEnumerator())
        {
            if (depth == 64)
            {
                Assert.True(budget.MoveNext(values));
                Assert.Equal(1, values.Current);
                Assert.False(budget.MoveNext(values));
            }
            else
                Assert.Equal("filter nesting limit exceeded", Assert.Throws<JqQuotaException>(() => budget.MoveNext(values)).Message);
        }
        using IEnumerator<int> next = Enumerate(1).GetEnumerator();
        Assert.True(budget.MoveNext(next));
        Assert.Equal(1, next.Current);
        Assert.False(budget.MoveNext(next));

        IEnumerable<int> Enumerate(int remaining)
        {
            if (remaining == 1)
            {
                yield return 1;
                yield break;
            }
            using IEnumerator<int> nested = Enumerate(remaining - 1).GetEnumerator();
            while (budget.MoveNext(nested)) yield return nested.Current;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_WideDestructuringUsesAdmittedIteration(bool objects)
    {
        const int count = 5000;
        string pattern = objects
            ? "{" + string.Join(',', Enumerable.Range(0, count).Select(index => "k" + index.ToString(CultureInfo.InvariantCulture) + ":$v" + index.ToString(CultureInfo.InvariantCulture))) + "}"
            : "[" + string.Join(',', Enumerable.Range(0, count).Select(index => "$v" + index.ToString(CultureInfo.InvariantCulture))) + "]";
        var memory = new BenchmarkMemoryBudget(32 * 1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "null as " + pattern + " | $v4999")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("null\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_DestructuringUnwindsSuspendedBranchesOnCancellationOrClosure(bool cancel)
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem { AppendRemainsOpen = cancel };
        host.AddFile("/first", "{\"a\":1,\"b\":2,\"c\":3,\"d\":4}\n");
        host.AddFile("/later", "{}\n");
        if (cancel)
            host.BeforeByteAppend = () => { if (host.AppendCallCount == 1) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            ". as {(\"a\",\"b\"):$x, (\"c\",\"d\"):$y} | [$x,$y]", "/first", "/later")));

        if (cancel)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
            Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal("[1,3]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.AppendCallCount);
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
        var next = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "42")));
        var reusable = new MockFileSystem();
        Assert.Equal(0, await next.ExecuteAsync(reusable, memory, CancellationToken.None));
        Assert.Equal("42\n", reusable.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
