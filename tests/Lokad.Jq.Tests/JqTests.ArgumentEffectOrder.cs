using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("--rawfile", "--help")]
    [InlineData("--slurpfile", "--help")]
    [InlineData("--rawfile", "--version")]
    [InlineData("--slurpfile", "--version")]
    [InlineData("--rawfile", "--unknown")]
    [InlineData("--slurpfile", "--unknown")]
    [InlineData("--rawfile", "--argjson")]
    [InlineData("--slurpfile", "--argjson")]
    [InlineData("--rawfile", "--from-file")]
    [InlineData("--slurpfile", "--from-file")]
    public async Task Jq_ArgumentFileFailurePrecedesLaterOptions(string option, string later)
    {
        string[] suffix = later switch
        {
            "--argjson" => [later, "j", "[", "-n", "42"],
            "--from-file" => [later, "/missing-program"],
            _ => [later],
        };
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", [option, "d", "/missing", .. suffix])));
        Assert.Equal(2, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: Bad JSON in " + option + " d /missing: cannot open /missing\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("--rawfile", false)]
    [InlineData("--rawfile", true)]
    [InlineData("--slurpfile", false)]
    [InlineData("--slurpfile", true)]
    public async Task Jq_ArgumentFileEffectsRespectHelpPosition(string option, bool helpFirst)
    {
        var host = new MockFileSystem();
        host.AddFile("/data", "42");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        string[] arguments = helpFirst ? ["--help", option, "d", "/data"] : [option, "d", "/data", "--help"];
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Contains("Usage:", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(helpFirst ? 0 : 1, host.ReadBytesCallCount);
        Assert.Equal(helpFirst ? 0 : 1, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ArgumentFileTerminalClosePrecedesLaterHelp(bool slurp)
    {
        var host = new MockFileSystem { CloseFailure = new JqOperationError("storage unavailable") };
        host.AddFile("/data", "42");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", slurp ? "--slurpfile" : "--rawfile", "d", "/data", "--help")));
        Assert.Equal(2, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error closing /data: storage unavailable\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ArgumentFileRefusalPrecedesLaterHelp()
    {
        var host = new MockFileSystem();
        host.AddFile("/data", new string('x', 256 * 1024));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--rawfile", "d", "/data", "--help")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ArgumentFileBindingCancelsBeforeLaterOptions()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem { BeforeByteRead = _ => cancellation.Cancel() };
        host.AddFile("/data", "42");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--rawfile", "d", "/data", "--help")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
