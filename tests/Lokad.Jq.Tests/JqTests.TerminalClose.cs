using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("json", "42\n", "42\n")]
    [InlineData("raw", "text\n", "\"text\"\n")]
    [InlineData("slurp", "42\n", "")]
    [InlineData("raw slurp", "text\n", "")]
    [InlineData("explicit", "42\n", "42\n")]
    [InlineData("stream", "[42]\n", "[[0],42]\n[[0]]\n")]
    [InlineData("sequence", "\u001e42\n", "\u001e42\n")]
    public async Task Jq_TerminalCloseFailureStopsBeforeOpeningLaterInput(string mode, string input, string output)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first", input);
        inner.AddFile("/later", "99\n");
        var host = new TerminalCloseHost(inner, "/first");
        string[] options = mode switch
        {
            "raw" => ["-Rc", "."],
            "slurp" => ["-sc", "."],
            "raw slurp" => ["-Rsc", "."],
            "explicit" => ["-nc", "try inputs catch \"caught\""],
            "stream" => ["--stream", "-c", "."],
            "sequence" => ["--seq", "-c", "."],
            _ => ["-c", "."],
        };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", [.. options, "/first", "/later"])));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error closing /first: storage unavailable\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(["/first"], host.Opened);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("program", "./program", 2)]
    [InlineData("rawfile", "./data", 2)]
    [InlineData("slurpfile", "./data", 2)]
    [InlineData("module", "/m.jq", 3)]
    [InlineData("data", "/m.json", 3)]
    [InlineData("metadata", "/m.jq", 5)]
    public async Task Jq_TerminalCloseFailureKeepsSourceAndHostDetail(string mode, string display, int status)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/program", "\"after\"");
        inner.AddFile("/data", "42\n");
        inner.AddFile("/m.jq", "def f:42;");
        inner.AddFile("/m.json", "42\n");
        string path = mode switch { "program" => "/program", "rawfile" or "slurpfile" => "/data", "data" => "/m.json", _ => "/m.jq" };
        var host = new TerminalCloseHost(inner, path);
        string[] arguments = mode switch
        {
            "program" => ["-n", "-f", "./program"],
            "rawfile" => ["-n", "--rawfile", "d", "./data", "\"after\""],
            "slurpfile" => ["-n", "--slurpfile", "d", "./data", "\"after\""],
            "module" => ["-n", "-L", "/", "import \"m\" as m {optional:true}; \"after\""],
            "data" => ["-n", "-L", "/", "import \"m\" as $m {optional:true}; \"after\""],
            _ => ["-n", "-L", "/", "try (\"m\" | modulemeta) catch \"caught\""],
        };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        Assert.Equal(status, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error closing " + display + ": storage unavailable\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("first(inputs)", 5, "42\n", "jq: error closing /first: storage unavailable\n")]
    [InlineData("halt", 5, "", "jq: error closing /first: storage unavailable\n")]
    [InlineData("\"bad\" | halt_error(7)", 7, "", "bad")]
    [InlineData("false", 5, "false\n", "jq: error closing /first: storage unavailable\n")]
    [InlineData("empty", 5, "", "jq: error closing /first: storage unavailable\n")]
    [InlineData("error(\"first\")", 5, "", "jq: error (at /first:1): first\n")]
    public async Task Jq_TerminalCloseFailureRespectsEstablishedOutcomes(string expression, int status, string output, string error)
    {
        var inner = new MockFileSystem(); inner.AddFile("/first", "42\n");
        var host = new TerminalCloseHost(inner, "/first");
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        string[] arguments = expression == "first(inputs)" ? ["-nec", expression, "/first"] : ["-ec", expression, "/first"];
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        Assert.Equal(status, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Jq_TerminalCloseFailureAfterOutputStopPreservesEarlierWriteFailure(int writeStatus)
    {
        var inner = new MockFileSystem(); inner.AddFile("/first", "42\n"); inner.AddFile("/later", "99\n");
        var host = new TerminalCloseHost(inner, "/first") { OutputStop = writeStatus == 0 ? JqAppendResult.Closed : JqAppendResult.Failure(new JqOperationError(JqOperationErrorKind.TransportFailure, "write failed")) };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".", "/first", "/later")));
        Assert.Equal(writeStatus == 0 ? 5 : writeStatus, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("42\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(writeStatus == 0 ? "jq: error closing /first: storage unavailable\n" : "jq: error writing <stdout>: write failed\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(["/first"], host.Opened);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_TerminalModuleCloseFailureStillClosesActiveInput()
    {
        var inner = new MockFileSystem(); inner.AddFile("/first", "42\n"); inner.AddFile("/m.jq", "def f:42;");
        var host = new TerminalCloseHost(inner, "/m.jq") { FailEveryClose = true };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "\"m\" | modulemeta", "/first")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("jq: error closing /m.jq: storage unavailable\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, inner.ClosedDescriptors.Count);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_TerminalCloseFailureAwaitsRetirementBeforeReportingAndRefunding()
    {
        var inner = new MockFileSystem(); inner.AddFile("/first", "42\n"); inner.AddFile("/later", "99\n");
        var host = new TerminalCloseHost(inner, "/first") { HoldClose = true };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".", "/first", "/later")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None);
        await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        Assert.True(memory.ReservedBytes > 0);
        Assert.Equal(["/first"], host.Opened);
        host.Release.SetResult();
        Assert.Equal(5, await execution);
        Assert.Equal("42\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error closing /first: storage unavailable\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_TerminalCloseFailurePreservesEarlierReadOrParseError(bool readFailure)
    {
        var inner = new MockFileSystem(); inner.AddFile("/first", "[!]"); inner.AddFile("/later", "42");
        var host = new TerminalCloseHost(inner, "/first") { ReadFailure = readFailure };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/first", "/later")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(readFailure ? "jq: error reading /first: storage unavailable\n"
            : "jq: parse error: '!' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 1.\n",
            inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(["/first"], host.Opened);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("program")]
    [InlineData("rawfile")]
    [InlineData("slurpfile")]
    public async Task Jq_TerminalCloseFailureReleasesUnpublishedFileBytesBeforeReporting(string mode)
    {
        const int limit = 128 * 1024;
        var inner = new MockFileSystem(); inner.AddFile("/first", new string(' ', 10000) + "1");
        var memory = new BenchmarkMemoryBudget(limit);
        IMemoryReservation? pressure = null;
        var host = new TerminalCloseHost(inner, "/first")
        {
            BeforeFailure = () => Assert.True(memory.TryReserve(limit - memory.ReservedBytes, out pressure)),
        };
        string[] arguments = mode == "program" ? ["-n", "-f", "/first"]
            : ["-n", "--" + mode, "d", "/first", "$d"];
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        Assert.Equal(2, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("jq: error closing /first: storage unavailable\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, inner.OpenFileCount);
    }

    private sealed class TerminalCloseHost(MockFileSystem inner, string failedPath) : IJqHost
    {
        private readonly Dictionary<JqFileDescriptor, string> _paths = [];
        private bool _failed;
        internal bool HoldClose { get; init; }
        internal bool ReadFailure { get; init; }
        internal bool FailEveryClose { get; init; }
        internal Action? BeforeFailure { get; init; }
        internal JqAppendResult? OutputStop { get; init; }
        internal List<string> Opened { get; } = [];
        internal TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken token) =>
            ReadFailure ? ValueTask.FromResult(JqByteReadResult.Failure(0, new JqOperationError(JqOperationErrorKind.TransportFailure, "storage unavailable"))) : inner.ReadBytesAsync(descriptor, buffer, token);
        public async Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken token)
        {
            var opened = await inner.OpenReadAsync(path, token).ConfigureAwait(false);
            if (opened.FileDescriptor is { } descriptor) { _paths[descriptor] = path.Path; Opened.Add(path.Path); }
            return opened;
        }
        public async Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            var result = await inner.AppendWhileOpenAsync(descriptor, bytes, token).ConfigureAwait(false);
            return descriptor == JqFileDescriptor.StdOut && OutputStop is { } stopped ? stopped : result;
        }
        public async Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken token)
        {
            Assert.False(token.CanBeCanceled);
            await inner.CloseDescriptorAsync(descriptor, token).ConfigureAwait(false);
            Closing.TrySetResult();
            if (HoldClose) await Release.Task.ConfigureAwait(false);
            if (FailEveryClose || !_failed && _paths[descriptor] == failedPath)
            {
                _failed = true;
                BeforeFailure?.Invoke();
                return JqCloseResult.Failure(new JqOperationError(JqOperationErrorKind.TransportFailure, "storage unavailable"));
            }
            return JqCloseResult.Success;
        }
    }
}
