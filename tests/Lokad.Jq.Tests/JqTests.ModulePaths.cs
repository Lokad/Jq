using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("./tiny", "function", "42\n")]
    [InlineData("./tiny", "include", "42\n")]
    [InlineData("./tiny", "data", "[42]\n")]
    [InlineData("./tiny", "metadata", "[\"value/0\"]\n")]
    [InlineData("dir/./tiny", "function", "43\n")]
    [InlineData("dir/./tiny", "include", "43\n")]
    [InlineData("dir/./tiny", "data", "[43]\n")]
    [InlineData("dir/./tiny", "metadata", "[\"value/0\"]\n")]
    [InlineData("dir//tiny", "function", "43\n")]
    [InlineData("dir//tiny", "include", "43\n")]
    [InlineData("dir//tiny", "data", "[43]\n")]
    [InlineData("dir//tiny", "metadata", "[\"value/0\"]\n")]
    public async Task Jq_ModulePathsNormalizeValidRelativeSpellings(string path, string kind, string expected)
    {
        string quoted = JsonSerializer.Serialize(path);
        string filter = kind switch
        {
            "function" => "import " + quoted + " as m; m::value",
            "include" => "include " + quoted + "; value",
            "data" => "import " + quoted + " as $d; $d",
            _ => quoted + "|modulemeta|.defs"
        };
        var host = new MockFileSystem();
        host.AddFile("/modules/tiny.jq", "def value:42;");
        host.AddFile("/modules/tiny.json", "42\n");
        host.AddFile("/modules/dir/tiny.jq", "def value:43;");
        host.AddFile("/modules/dir/tiny.json", "43\n");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/modules", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ModulePathsShareCanonicalCacheKeysAndSourceAttribution()
    {
        var host = new MockFileSystem();
        host.AddFile("/modules/tiny.jq", "def location:$__loc__;");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/modules",
            "import \"./tiny\" as a; import \"tiny\" as b; [a::location,b::location]")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("[{\"file\":\"/modules/tiny.jq\",\"line\":1},{\"file\":\"/modules/tiny.jq\",\"line\":1}]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("bundle.jq")]
    [InlineData("bundle/jq/main.jq")]
    [InlineData("bundle/bundle.jq")]
    public async Task Jq_ModulePathsPreserveFilenameVariants(string candidate)
    {
        var host = new MockFileSystem();
        host.AddFile("/modules/" + candidate, "def value:42;");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/modules", "import \"./bundle\" as m; m::value")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("42\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
    }

    [Theory]
    [InlineData("function")]
    [InlineData("data")]
    [InlineData("metadata")]
    [InlineData("cached")]
    [InlineData("program")]
    public async Task Jq_ModulePathsAdmitActualLongPathConstruction(string kind)
    {
        string directory = "/" + new string('d', 16384);
        var host = new MockFileSystem();
        host.AddFile(directory + "/tiny.jq", "def value:42;");
        host.AddFile(directory + "/tiny.json", "42\n");
        host.AddFile(directory + "/main.jq", "import \"./tiny\" as m; m::value");
        string filter = kind switch
        {
            "data" => "import \"tiny\" as $d; $d", "metadata" => "\"tiny\"|modulemeta|.defs",
            "cached" => "import \"tiny\" as a; import \"./tiny\" as b; [a::value,b::value]",
            _ => "import \"tiny\" as m; m::value"
        };
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            kind == "program" ? ["-nc", "-f", directory + "/main.jq"] : ["-nc", filter], [new("PWD", directory)]);
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(kind == "data" ? "[42]\n" : kind == "metadata" ? "[\"value/0\"]\n" : kind == "cached" ? "[42,42]\n" : "42\n",
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(kind == "program" ? 2 : 1, host.ReadBytesCallCount);
        Assert.Equal(kind == "program" ? 2 : 1, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(65536, 0)]
    [InlineData(131072, 1)]
    public async Task Jq_ModulePathsRefuseActualCandidateOrLabelConstruction(int allowance, int reads)
    {
        string directory = "/" + new string('d', 40000);
        var host = new MockFileSystem();
        host.AddFile(directory + "/tiny.jq", "def value:42;");
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", "import \"tiny\" as m {optional:true}; 42"], [new("PWD", directory)]);
        var memory = new BenchmarkMemoryBudget(allowance);
        Assert.Equal(5, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(reads, host.ReadBytesCallCount);
        Assert.Equal(reads, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ModulePathsRetainAdmissionUntilPendingOpenEnds(bool cancelled)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/modules/tiny.jq", "def value:42;");
        var host = new PendingModuleOpenHost(inner);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/modules",
            "import \"./tiny\" as m; m::value")));
        Task<int> execution = command.ExecuteAsync(host, memory, cancellation.Token);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(execution.IsCompleted);
            long retained = memory.ReservedBytes;
            Assert.True(retained > 0);
            if (cancelled) cancellation.Cancel();
            Assert.False(execution.IsCompleted);
            Assert.Equal(retained, memory.ReservedBytes);
        }
        finally { host.Release.TrySetResult(); }
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else Assert.Equal(0, await execution);
        Assert.Equal(cancelled ? "" : "42\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(cancelled ? 0 : 1, inner.ClosedDescriptors.Count);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class PendingModuleOpenHost(MockFileSystem inner) : IJqHost
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken) => inner.ReadBytesAsync(descriptor, buffer, cancellationToken);
        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) => inner.CloseDescriptorAsync(descriptor, cancellationToken);
        public async Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken)
        {
            if (path.Path == "/modules/tiny.jq")
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
            return await inner.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }
}
