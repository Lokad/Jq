using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("--arg")]
    [InlineData("--argjson")]
    [InlineData("--rawfile")]
    [InlineData("--slurpfile")]
    public async Task Jq_NamedArgumentAliasesPreservePathIdentity(string option)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/data", "{\"a\":1}\n");
        bool file = option is "--rawfile" or "--slurpfile";
        string value = file ? "/data" : option == "--argjson" ? "{\"a\":1}" : "abc";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", option, "data", value,
            "$ARGS.named.data | path($data)")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("[]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(file ? 1 : 0, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ArgumentContainersFitWithoutDuplicatingParsedTrees(bool positional)
    {
        string json = "[" + string.Join(',', Enumerable.Repeat("null", 10000)) + "]";
        string[] arguments = positional ? ["-nc", "$ARGS.positional[0] | length", "--jsonargs", json]
            : ["-nc", "--argjson", "data", json, "$data | length"];
        var root = new BenchmarkMemoryBudget(384 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, [])));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("10000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_NamedArgumentBorrowSurvivesPendingOutputAndCancellation(bool cancel)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var inner = new MockFileSystem();
        inner.AddFile("/data", "{\"a\":1}\n");
        var host = new MemoryBorrowHost(inner);
        using var cancellation = new CancellationTokenSource();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--slurpfile", "data", "/data",
            "$ARGS.named.data | path($data)")));
        Task<int> execution = command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token);
        Assert.Equal("[]\n"u8.ToArray(), (await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10))).ToArray());
        long charged = root.ReservedBytes;
        Assert.True(charged > 0);
        if (cancel) cancellation.Cancel();
        Assert.False(execution.IsCompleted);
        Assert.Equal(charged, root.ReservedBytes);
        host.Release.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else Assert.Equal(0, await execution);
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
