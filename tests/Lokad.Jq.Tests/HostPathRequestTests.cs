using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class HostPathRequestTests
{
    [Theory]
    [InlineData("input", "2\n")]
    [InlineData("program", "2\n")]
    [InlineData("rawfile", "\"2\\n\"\n")]
    [InlineData("slurpfile", "[2]\n")]
    [InlineData("library", "2\n")]
    [InlineData("program-import", "2\n")]
    [InlineData("search", "2\n")]
    public async Task LookupRetainsLinkTraversalAndCallerDirectory(string mode, string output)
    {
        var inner = new MockFileSystem();
        var host = new TraversalHost(inner);
        string[] arguments = mode switch
        {
            "input" => ["-c", ".", "link/../input.json"],
            "program" => ["-nc", "-f", "link/../input.json"],
            "rawfile" => ["-nc", "--rawfile", "x", "link/../input.json", "$x"],
            "slurpfile" => ["-nc", "--slurpfile", "x", "link/../input.json", "$x"],
            "library" => ["-nc", "-L", "link/..", "import \"lib/m\" as m; m::value"],
            "program-import" => ["-nc", "-f", "link/../main.jq"],
            _ => ["-nc", "import \"m\" as m {search: \"link/..\"}; m::value"]
        };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, [new("PWD", "/work")]);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Contains(host.Requests, path => path.Path.Contains("link/../", StringComparison.Ordinal));
        Assert.All(host.Requests, path => Assert.Equal("/work", path.CurrentDirectory.Path));
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("missing/../input.json", "path component missing")]
    [InlineData("link/../input.json/", "not a directory")]
    public async Task FailedTraversalKeepsSourceSpelling(string spelling, string detail)
    {
        var inner = new MockFileSystem();
        var host = new TraversalHost(inner);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", [".", spelling], [new("PWD", "/work")]);
        Assert.Equal(2, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: cannot open " + spelling + ": " + detail + "\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(spelling, Assert.Single(host.Requests).Path);
        Assert.Empty(inner.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    // The namespace contains /work/input.json = 1, a link /work/link to
    // /target/nested, and /target/input.json = 2. Its table deliberately makes
    // link/.. differ from lexical normalization. /work/missing does not exist.
    private sealed class TraversalHost : IJqHost
    {
        private readonly MockFileSystem _inner;
        internal List<JqPathRequest> Requests { get; } = [];

        internal TraversalHost(MockFileSystem inner)
        {
            _inner = inner;
            inner.AddFile("/target/input.json", "2\n");
            inner.AddFile("/target/main.jq", "import \"m\" as m; m::value");
            inner.AddFile("/target/m.jq", "def value: 2;");
            inner.AddFile("/target/lib/m.jq", "def value: 2;");
            inner.AddFile("/work/input.json", "1\n");
            inner.AddFile("/work/m.jq", "def value: 1;");
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(path);
            if (path.Path.Contains("missing/", StringComparison.Ordinal))
                return Task.FromResult(JqOpenedFile.Failure(new JqOperationError(JqOperationErrorKind.NotFound, "path component missing")));
            if (path.Path.EndsWith('/'))
                return Task.FromResult(JqOpenedFile.Failure(new JqOperationError(JqOperationErrorKind.InvalidPathKind, "not a directory")));
            string location = path.Path.StartsWith('/') ? path.Path : path.CurrentDirectory.Path + "/" + path.Path;
            location = location.Replace("/work/link/../", "/target/", StringComparison.Ordinal);
            return _inner.OpenReadAsync(new JqPathRequest(location, path.CurrentDirectory), token);
        }

        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken token) =>
            _inner.ReadBytesAsync(descriptor, buffer, token);
        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken token) =>
            _inner.AppendWhileOpenAsync(descriptor, content, token);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken token) => _inner.CloseDescriptorAsync(descriptor, token);
    }
}
