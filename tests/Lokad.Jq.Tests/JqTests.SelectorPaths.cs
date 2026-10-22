using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("first(.a)", "[\"a\"]\n")]
    [InlineData("first(.a,error(\"late\"))", "[\"a\"]\n")]
    [InlineData("limit(1;.a,error(\"late\"))", "[\"a\"]\n")]
    [InlineData("limit(0;error(\"late\"))", "")]
    [InlineData("nth(1;.a,.b)", "[\"b\"]\n")]
    [InlineData("skip(1;.a,.b)", "[\"b\"]\n")]
    [InlineData("limit((1,2);(.a,.b))", "[\"a\"]\n[\"a\"]\n[\"b\"]\n")]
    [InlineData("nth((0,1);(.a,.b))", "[\"a\"]\n[\"b\"]\n")]
    [InlineData("skip((0,1);(.a,.b))", "[\"a\"]\n[\"b\"]\n[\"b\"]\n")]
    [InlineData("limit(2;repeat(.a))", "[\"a\"]\n[\"a\"]\n")]
    [InlineData("limit(2;recurse(.[]?))", "[]\n[\"a\"]\n")]
    [InlineData("until(type == \"number\";.a)", "[\"a\"]\n")]
    [InlineData("while(type != \"number\";.a)", "[]\n")]
    public async Task Jq_SelectorsAndLoopsPreserveSelectedPaths(string source, string expected)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":1,\"b\":2}");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "path(" + source + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("first(.a)", "{\"a\":7,\"b\":2}\n")]
    [InlineData("nth(1;.a,.b)", "{\"a\":1,\"b\":7}\n")]
    [InlineData("skip(1;.a,.b)", "{\"a\":1,\"b\":7}\n")]
    [InlineData("limit(2;.a,.b)", "{\"a\":7,\"b\":7}\n")]
    public async Task Jq_SelectorPathsRemainUpdateTargets(string source, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":1,\"b\":2}");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "(" + source + ") |= 7")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_FirstPathFailurePrecedesLaterEffectsAndKeepsBindingRecovery()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":1}");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "(try path(first([.] | .[0] | debug)) catch \"caught\"), (. as $saved | path(first([.] | $saved))), (try path(last(.a)) catch startswith(\"Invalid path\"))")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"caught\"\n[]\ntrue\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("path(repeat(empty))")]
    [InlineData("path(until(false;.))")]
    [InlineData("path(skip(1000000000;repeat(.)))")]
    [InlineData("path(nth(1000000000;repeat(.)))")]
    public async Task Jq_PathSelectionCancelsWithoutProducingAndClosesOwnedInput(string source)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.AddFile("/input", "null");
        host.AddFile("/later", "invalid JSON");
        host.BeforeByteAppend = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (" + source + ")", "/input", "/later")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => command.ExecuteAsync(host, root, cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
