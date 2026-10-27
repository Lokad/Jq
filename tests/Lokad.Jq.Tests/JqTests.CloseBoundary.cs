using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false, "try inputs catch .", "42\n")]
    [InlineData(true, "try inputs catch .", "42\n")]
    [InlineData(false, "first(inputs)", "42\n")]
    [InlineData(true, "first(inputs)", "42\n")]
    [InlineData(false, "try (\"m\" | modulemeta) catch .", "")]
    [InlineData(true, "try (\"m\" | modulemeta) catch .", "")]
    public async Task Jq_CloseContractFailuresEscapeLanguageHandlers(bool overflow, string filter, string output)
    {
        var source = new MockFileSystem();
        source.AddFile("/input", "42\n");
        source.AddFile("/m.jq", "def f: .;");
        var host = new CloseFaultHost(source, overflow);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/", filter, "/input")));
        var failure = await Assert.ThrowsAsync<JqHostFailureException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.IsType(overflow ? typeof(OverflowException) : typeof(ArgumentException), failure.InnerException);
        Assert.Equal(output, source.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(source.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, source.OpenFileCount);
        Assert.Single(source.ClosedDescriptors);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_CloseContractFailureDoesNotReplaceEarlierCancellationOrRefusal(bool cancel)
    {
        var source = new MockFileSystem();
        source.AddFile("/input", "42\n");
        using var cancellation = new CancellationTokenSource();
        if (cancel) source.BeforeByteRead = _ => cancellation.Cancel();
        var root = new BenchmarkMemoryBudget(cancel ? 64 * 1024 : 8192);
        var host = new CloseFaultHost(source, false);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/input")));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token));
        else
        {
            Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token));
            Assert.Equal("jq: memory budget exceeded\n", source.GetOutput(JqFileDescriptor.StdErr));
        }
        Assert.Empty(source.GetOutput(JqFileDescriptor.StdOut));
        Assert.Single(source.ClosedDescriptors);
        Assert.Equal(0, source.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("inputs")]
    [InlineData("first(inputs)")]
    public async Task Jq_PendingCloseRetainsAdmissionUntilContractFailureCompletes(string filter)
    {
        var source = new MockFileSystem();
        source.AddFile("/input", "42\n");
        var host = new CloseFaultHost(source, false) { HoldClose = true };
        var root = new BenchmarkMemoryBudget(64 * 1024);
        string[] arguments = filter == "." ? [filter, "/input"] : ["-n", filter, "/input"];
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        Task<int> pending = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None);
        await host.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.True(root.ReservedBytes > 0);
        host.CloseRelease.SetResult();
        await Assert.ThrowsAsync<JqHostFailureException>(() => pending);
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal("42\n", source.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_FileVariableCloseContractFailureRefundsItsReadResult()
    {
        var source = new MockFileSystem();
        source.AddFile("/input", "42\n");
        var host = new CloseFaultHost(source, false);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--slurpfile", "v", "/input", "$v")));
        await Assert.ThrowsAsync<JqHostFailureException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(0, source.OpenFileCount);
        Assert.Single(source.ClosedDescriptors);
        Assert.Equal(0, root.ReservedBytes);
    }

    private sealed class CloseFaultHost(MockFileSystem inner, bool overflow) : IJqHost
    {
        internal bool HoldClose { get; init; }
        internal TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken) => inner.ReadBytesAsync(descriptor, buffer, cancellationToken);
        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);
        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);
        public async Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            await inner.CloseDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
            CloseEntered.TrySetResult();
            if (HoldClose) await CloseRelease.Task.ConfigureAwait(false);
            if (overflow) throw new OverflowException("close contract failure");
            throw new ArgumentException("close contract failure");
        }
    }
}
