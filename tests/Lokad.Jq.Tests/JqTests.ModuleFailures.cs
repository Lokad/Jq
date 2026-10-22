using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    // jq 1.8.2 resolves a candidate before loading it. An unreadable selected
    // file does not cause a search of later directories, even for optional imports.
    [Theory]
    [InlineData("import \"m\" as m; m::f", false, false, 3)]
    [InlineData("include \"m\"; f", false, true, 3)]
    [InlineData("import \"m\" as $d; $d", true, false, 3)]
    [InlineData("import \"m\" as $d {raw:true}; $d", true, true, 3)]
    [InlineData("\"m\" | modulemeta", false, true, 5)]
    [InlineData("import \"m\" as m {optional:true}; 1", false, false, 0)]
    [InlineData("include \"m\" {optional:true}; 1", false, true, 0)]
    [InlineData("import \"m\" as $d {optional:true}; 1", true, true, 0)]
    [InlineData("import \"m\" as $d {optional:true,raw:true}; 1", true, false, 0)]
    public async Task Jq_SelectedModuleReadFailureDoesNotSearchLaterFiles(
        string program, bool data, bool partial, int status)
    {
        var inner = new MockFileSystem();
        string suffix = data ? ".json" : ".jq";
        string selected = "/first/m" + suffix;
        inner.AddFile(selected, data ? "1\n" : "def f:1;");
        inner.AddFile("/second/m" + suffix, data ? "2" : "def f:2;");
        var host = new ModuleReadFailureHost(inner, selected, partial);
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildModuleInvocation("-nrc", "-L", "/first", "-L", "/second", program)));

        Assert.Equal(status, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(status == 0 ? "1\n" : "", inner.GetOutput(JqFileDescriptor.StdOut));
        string message = "error loading " + (data ? "data file " : "module ") + selected + ": input read failed\n";
        Assert.Equal(status == 0 ? "" : (status == 5 ? "jq: error (at <unknown>): " : "jq: ") + message,
            inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.DoesNotContain(host.Paths, path => path.StartsWith("/second/", StringComparison.Ordinal));
        Assert.Equal(partial ? 2 : 1, host.Reads);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_OptionalUnreadableModuleDoesNotBindFunctionsFromLaterFile()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first/m.jq", "def f:1;");
        inner.AddFile("/second/m.jq", "def f:2;");
        var host = new ModuleReadFailureHost(inner, "/first/m.jq", false);
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildModuleInvocation("-n", "-L", "/first", "-L", "/second",
            "import \"m\" as m {optional:true}; m::f")));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error (at <unknown>): undefined function m::f/0\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.DoesNotContain(host.Paths, path => path.StartsWith("/second/", StringComparison.Ordinal));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_OptionalModuleStillReportsItsSyntaxError()
    {
        var host = new MockFileSystem();
        host.AddFile("/first/m.jq", "def f: (;");
        host.AddFile("/second/m.jq", "def f:2;");
        var result = await RunModulesAsync(host, "-n", "-L", "/first", "-L", "/second",
            "import \"m\" as m {optional:true}; 1");
        Assert.Equal(3, result.Exit);
        Assert.Empty(result.Out);
        Assert.Contains("/first/m.jq", result.Err, StringComparison.Ordinal);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_SelectedModuleReadFailureSurvivesCleanupFault(bool optional)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first/m.jq", "def f:1;");
        inner.AddFile("/second/m.jq", "def f:2;");
        var host = new ModuleReadFailureHost(inner, "/first/m.jq", false)
        {
            CloseFailure = new ArgumentException("close sentinel"),
        };
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildModuleInvocation("-n", "-L", "/first", "-L", "/second",
            "import \"m\" as m {optional:" + (optional ? "true" : "false") + "}; 1")));
        Assert.Equal(optional ? 0 : 3, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(optional ? "1\n" : "", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(optional ? "" : "jq: error loading module /first/m.jq: input read failed\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.DoesNotContain(host.Paths, path => path.StartsWith("/second/", StringComparison.Ordinal));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class ModuleReadFailureHost(MockFileSystem inner, string selected, bool partial) : IJqHost
    {
        private JqFileDescriptor? _selected;
        internal List<string> Paths { get; } = [];
        internal int Reads { get; private set; }
        internal Exception? CloseFailure { get; init; }

        public async Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken)
        {
            Paths.Add(path.Path);
            var opened = await inner.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (path.Path == selected) _selected = opened.FileDescriptor;
            return opened;
        }

        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (descriptor != _selected) return inner.ReadBytesAsync(descriptor, buffer, cancellationToken);
            return partial && Reads == 1 ? inner.ReadBytesAsync(descriptor, buffer[..1], cancellationToken)
                : ValueTask.FromResult(JqByteReadResult.ReadFailure);
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);
        public async Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
        {
            int status = await inner.CloseDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (CloseFailure is { } failure) throw failure;
            return status;
        }
    }
}
