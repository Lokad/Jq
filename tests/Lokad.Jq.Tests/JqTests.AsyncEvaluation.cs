using System.Collections.Concurrent;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("input", "1\n")]
    [InlineData("first(inputs)", "1\n")]
    [InlineData("[inputs]", "[1,2,3]\n")]
    [InlineData("reduce inputs as $x (0; .+$x)", "6\n")]
    [InlineData("foreach inputs as $x (0; .+$x)", "1\n3\n6\n")]
    [InlineData("def f($x): $x+1; f(input)", "2\n")]
    [InlineData("def f(g): [g,g]; f(input)", "[1,2]\n")]
    [InlineData("input as $x | [$x,input]", "[1,2]\n")]
    [InlineData("{(input|tostring):input}", "{\"1\":2}\n")]
    [InlineData("\"\\(input)-\\(input)\"", "\"2-1\"\n")]
    [InlineData("[3,1,2] | sort_by(input)", "[3,1,2]\n")]
    [InlineData("[0,0] | map_values(input)", "[1,2]\n")]
    [InlineData("{a:0} | .a |= input", "{\"a\":1}\n")]
    [InlineData("{a:0} | path(if input==1 then .a else empty end)", "[\"a\"]\n")]
    [InlineData("try (input,error(\"late\")) catch .", "1\n\"late\"\n")]
    [InlineData("limit(input;inputs)", "2\n")]
    [InlineData("nth(input;inputs)", "3\n")]
    [InlineData("any(inputs;.==2)", "true\n")]
    [InlineData("add(inputs)", "6\n")]
    [InlineData("until(.==3;input)", "3\n")]
    [InlineData("[0 | recurse(input; .<=2)]", "[0,1,2]\n")]
    [InlineData("[0,0] | walk(if type==\"number\" then input else . end)", "[1,2]\n")]
    [InlineData("limit(1;repeat(input))", "1\n")]
    [InlineData("[0,1] | group_by(input%2)", "[[1],[0]]\n")]
    [InlineData("[0,1] | min_by(input)", "0\n")]
    [InlineData("\"a\" | gsub(\"a\"; input|tostring)", "\"1\"\n")]
    [InlineData("{\"1\":42} as {(input|tostring):$v} | $v", "42\n")]
    [InlineData("def f($n): if $n==0 then input else f($n-1) end; f(1000)", "1\n")]
    [InlineData("\"m\" | modulemeta | .kind", "\"ok\"\n")]
    public async Task Jq_PendingHostReadSuspendsThroughFilters(string body, string expected)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/input", "1\n2\n3\n");
        inner.AddFile("/m.jq", "module {kind:\"ok\"}; def f: .;");
        var host = new EvaluationReadHost(inner);
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/",
            "\"ready\", (" + body + ")", "/input")));

        Task<int> execution = command.ExecuteAsync(host, root, cancellation.Token);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(execution.IsCompleted);
            Assert.Equal("\"ready\"\n", inner.GetOutput(JqFileDescriptor.StdOut));
            Assert.True(root.ReservedBytes > 0);
        }
        finally { host.Release.TrySetResult(); }
        Assert.Equal(0, await execution);
        Assert.Equal("\"ready\"\n" + expected, inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try input catch .")]
    [InlineData("first(inputs)")]
    [InlineData("\"m\" | modulemeta")]
    public async Task Jq_PendingEvaluationReadCancelsAndRefunds(string body)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/input", "1");
        inner.AddFile("/m.jq", "def f: .;");
        var host = new EvaluationReadHost(inner);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/", body, "/input")));
        Task<int> execution = command.ExecuteAsync(host, root, cancellation.Token);
        await host.Entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ConcurrentPendingReadsShareAdmissionWithoutWorkers()
    {
        const int count = 100;
        var root = new BenchmarkMemoryBudget(16 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "first(inputs)", "/input")));
        var hosts = Enumerable.Range(0, count).Select(_ =>
        {
            var inner = new MockFileSystem();
            inner.AddFile("/input", "42\n");
            return new EvaluationReadHost(inner);
        }).ToArray();
        Task<int>[] executions = hosts.Select(host => command.ExecuteAsync(host, root, cancellation.Token)).ToArray();
        try
        {
            Assert.All(hosts, host => Assert.True(host.Entered.Task.IsCompletedSuccessfully));
            Assert.All(executions, execution => Assert.False(execution.IsCompleted));
            Assert.True(root.ReservedBytes > 0);
        }
        finally { foreach (var host in hosts) host.Release.TrySetResult(); }
        Assert.All(await Task.WhenAll(executions), status => Assert.Equal(0, status));
        Assert.All(hosts, host =>
        {
            Assert.Equal("42\n", host.Inner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Single(host.Inner.ClosedDescriptors);
            Assert.Equal(0, host.Inner.OpenFileCount);
        });
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("first(inputs)")]
    [InlineData("\"m\" | modulemeta")]
    public async Task Jq_EvaluationReturnsToCapturedHostContext(string body)
    {
        var context = new EvaluationContext();
        var inner = new MockFileSystem();
        inner.AddFile("/input", "42");
        inner.AddFile("/m.jq", "def f: .;");
        var host = new EvaluationReadHost(inner);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var returned = new TaskCompletionSource<Task<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/", body, "/input")));
        new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { returned.SetResult(command.ExecuteAsync(host, root, cancellation.Token)); }
            catch (Exception exception) { returned.SetException(exception); }
        }) { IsBackground = true }.Start();
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool returnedBeforeRelease;
        try
        {
            // The host deliberately captures this context. Cancellation can only
            // finish after its posted continuation is pumped by the caller.
            returnedBeforeRelease = await Task.WhenAny(returned.Task, Task.Delay(TimeSpan.FromSeconds(2))) == returned.Task;
        }
        finally
        {
            cancellation.Cancel();
            await context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            context.Pump();
        }
        Task<int> execution = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(returnedBeforeRelease);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_PureEvaluationCompletesSynchronously()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "[range(10)|.+1]")));
        Task<int> execution = command.ExecuteAsync(host, CancellationToken.None);
        Assert.True(execution.IsCompletedSuccessfully);
        Assert.Equal(0, await execution);
        Assert.Equal("[1,2,3,4,5,6,7,8,9,10]\n", host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ResumedReadPreservesRefusalAndDownstreamClosure(bool refuse)
    {
        var inner = new MockFileSystem { AppendRemainsOpen = false };
        inner.AddFile("/input", "42\n");
        inner.AddFile("/later", "99");
        var host = new EvaluationReadHost(inner);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            refuse ? "try (input | \"x\" * 1000000) catch \"caught\"" : "inputs", "/input", "/later")));
        Task<int> execution = command.ExecuteAsync(host, root, cancellation.Token);
        await host.Entered.Task;
        host.Release.SetResult();
        Assert.Equal(refuse ? 5 : 0, await execution);
        Assert.Equal(refuse ? "" : "42\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(refuse ? "jq: memory budget exceeded\n" : "", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(1, inner.ReadBytesCallCount);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InputRepeatedlySuspendsWithinBoundedMemory()
    {
        const int records = 131072;
        var inner = new MockFileSystem();
        inner.AddFile("/input", "");
        var host = new EvaluationStreamHost(inner, records);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "input | empty", "/input")));

        int status = await command.ExecuteAsync(host, memory, cancellation.Token);
        Assert.True(status == 0, inner.GetOutput(JqFileDescriptor.StdErr) + $"records={host.RecordsRead}; peak={root.PeakBytes}");
        Assert.Equal(records, host.RecordsRead);
        Assert.True(host.ReadCalls >= 32);
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.Counts.Refusals);
        Assert.Equal(0, root.ReservedBytes);
    }

    private sealed class EvaluationStreamHost(MockFileSystem inner, int records) : IJqHost
    {
        internal int RecordsRead { get; private set; }
        internal int ReadCalls { get; private set; }
        public async ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            // A real asynchronous boundary for every bounded batch.
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            ReadCalls++;
            // Odd batches alternate suspended reads between outer iteration and
            // explicit input, while each completed outer record releases demand.
            int count = Math.Min(records - RecordsRead, Math.Min(4095, buffer.Length / 2));
            for (int i = 0; i < count; i++)
            {
                buffer.Span[2 * i] = (byte)'0';
                buffer.Span[2 * i + 1] = (byte)'\n';
            }
            RecordsRead += count;
            return JqByteReadResult.Success(count * 2, RecordsRead == records);
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);
        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) => inner.CloseDescriptorAsync(descriptor, cancellationToken);
    }

    private sealed class EvaluationReadHost(MockFileSystem inner) : IJqHost
    {
        internal MockFileSystem Inner => inner;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var reading = Read();
            Entered.TrySetResult();
            return reading;

            async ValueTask<JqByteReadResult> Read()
            {
                // Intentionally keep the host's context, as an embedding host may.
                await Release.Task.WaitAsync(cancellationToken);
                return await inner.ReadBytesAsync(descriptor, buffer, cancellationToken);
            }
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);
        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) => inner.CloseDescriptorAsync(descriptor, cancellationToken);
    }

    private sealed class EvaluationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        internal TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Enqueue((callback, state));
            Posted.TrySetResult();
        }
        internal void Pump()
        {
            while (_pending.TryDequeue(out var work)) work.Callback(work.State);
        }
    }
}
