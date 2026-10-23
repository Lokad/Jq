using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("import \"child\" as c; def f: c::f;", "child")]
    [InlineData("import \"child\" as $d; def f: $d;", "child")]
    [InlineData("include \"child\"; def f: absent;", "child")]
    [InlineData("def f: absent;", null)]
    [InlineData("def f: $absent;", null)]
    [InlineData("def f: {$absent};", null)]
    [InlineData("def f: \"\\(absent)\";", null)]
    [InlineData("def f: break $absent;", null)]
    [InlineData("def f: def g: absent; g;", null)]
    public async Task Jq_ModuleInspectionDoesNotResolveBodiesOrDependencies(string body, string? dependency)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/lib/m.jq", "module {version:1};" + body);
        inner.AddFile("/lib/child.jq", "import \"m\" as m; def g: 0;");
        inner.AddFile("/lib/child.json", "[");
        inner.BeforeByteRead = _ => Assert.Equal(1, inner.ReadBytesCallCount);
        var host = new InspectionHost(inner);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/lib",
            "\"m\" | modulemeta | [.version,.defs,(.deps|map(.relpath))]")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        string deps = dependency is null ? "[]" : "[\"child\"]";
        Assert.Equal("[1,[\"f/0\"]," + deps + "]\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.DoesNotContain(host.Opened, path => path.Contains("child", StringComparison.Ordinal));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ModuleInspectionReadsFreshMetadataWithoutRelinkingImports()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/lib/m.jq", "module {version:1}; def f: 42;");
        var host = new InspectionHost(inner);
        host.AfterAppend = () => inner.AddFile("/lib/m.jq", "module {version:2}; def f: 99;");
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/lib",
            "import \"m\" as m; (\"m\"|modulemeta|.version),(\"m\"|modulemeta|.version),m::f")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("1\n2\n42\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(3, inner.ReadBytesCallCount);
        Assert.Equal(3, inner.ClosedDescriptors.Count);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ModuleInspectionStreamsAcrossRecordsWithinSharedAllowance(bool distinct)
    {
        const int count = 1000;
        var host = new MockFileSystem();
        for (int i = 0; i < (distinct ? count : 1); i++)
            host.AddFile("/lib/m" + i + ".jq", "module {version:1}; def f: absent;");
        host.SetStandardInput(string.Concat(Enumerable.Range(0, count).Select(i => "\"m" + (distinct ? i : 0) + "\"\n")));
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "-L", "/lib", "modulemeta | .version")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("1\n", count)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(count, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData("def f: (;")]
    [InlineData("def f: 1")]
    [InlineData("42")]
    [InlineData("def f: else;")]
    [InlineData("def f: range();")]
    public async Task Jq_ModuleInspectionStillValidatesLibrarySyntax(string source)
    {
        var host = new MockFileSystem();
        host.AddFile("/m.jq", source);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "\"m\"|modulemeta")));
        Assert.Equal(5, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Contains("(file \"/m.jq\")", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
    }

    [Fact]
    public async Task Jq_ModuleInspectionHonorsCancellationBeforeAnotherQuery()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/m.jq", "def f: 42;");
        var host = new InspectionHost(inner);
        using var cancellation = new CancellationTokenSource();
        host.AfterAppend = cancellation.Cancel;
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "(\"m\"|modulemeta|.defs), (\"m\"|modulemeta|.defs)")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal("[\"f/0\"]\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class InspectionHost(MockFileSystem inner) : IJqHost
    {
        internal List<string> Opened { get; } = [];
        internal Action? AfterAppend { get; set; }
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> bytes, CancellationToken token) =>
            inner.ReadBytesAsync(descriptor, bytes, token);
        public async Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            var result = await inner.AppendWhileOpenAsync(descriptor, bytes, token);
            AfterAppend?.Invoke();
            return result;
        }
        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken token)
        {
            Opened.Add(path.Path);
            return inner.OpenReadAsync(path, token);
        }
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken token) =>
            inner.CloseDescriptorAsync(descriptor, token);
    }
}
