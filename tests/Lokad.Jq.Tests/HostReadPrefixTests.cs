using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class HostReadPrefixTests
{
    [Theory]
    [InlineData("1\n2\n{", "inputs", "1\n2\n")]
    [InlineData("1\n2", "inputs", "1\n")]
    [InlineData("\"ready\"", "inputs", "\"ready\"\n")]
    [InlineData("true\n", "inputs", "true\n")]
    [InlineData("1\n2\n", "first(inputs)", "1\n")]
    [InlineData("1\n", "try inputs catch \"caught\"", "1\n")]
    public async Task CompletePrefixValuesPrecedeAnUncatchableHostFailure(string prefix, string filter, string output)
    {
        var inner = new MockFileSystem
        {
            CloseFailure = new JqOperationError(JqOperationErrorKind.TransportFailure, "close failed")
        };
        inner.AddFile("/input", "");
        var host = new PrefixHost(inner, prefix);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", ["-nc", filter, "/input"], [])));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error reading /input: connection lost\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.Reads);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("-R", "a\nb", "\"a\"\n")]
    [InlineData("--stream", "[1,", "[[0],1]\n")]
    [InlineData("--seq", "\u001e1\n\u001e2", "\u001e1\n")]
    public async Task FramingDoesNotFinalizeAnIncompleteTailOnFailure(string mode, string prefix, string output)
    {
        var inner = new MockFileSystem();
        var host = new PrefixHost(inner, prefix);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", [mode, "-c", "."], [])));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(output, inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error reading <stdin>: connection lost\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.Reads);
        Assert.Empty(inner.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task DefinitiveParseFailureInThePrefixRemainsPrimary()
    {
        var inner = new MockFileSystem();
        var host = new PrefixHost(inner, "1\n[!");
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-c", "."], [])));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("1\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.StartsWith("jq: parse error:", inner.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        Assert.DoesNotContain("connection lost", inner.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
    }

    private sealed class PrefixHost(MockFileSystem inner, string prefix) : IJqHost
    {
        internal int Reads { get; private set; }

        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(1, ++Reads);
            int count = Encoding.UTF8.GetBytes(prefix, buffer.Span);
            return ValueTask.FromResult(JqByteReadResult.Failure(count,
                new JqOperationError(JqOperationErrorKind.TransportFailure, "connection lost")));
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken token) =>
            inner.AppendWhileOpenAsync(descriptor, content, token);
        public Task<JqOpenedFile> OpenReadAsync(JqPathRequest path, CancellationToken token) => inner.OpenReadAsync(path, token);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken token) => inner.CloseDescriptorAsync(descriptor, token);
    }
}
