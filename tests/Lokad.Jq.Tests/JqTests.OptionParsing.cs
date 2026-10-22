using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("-n", "--null-input")]
    [InlineData("-R", "--raw-input")]
    [InlineData("-s", "--slurp")]
    [InlineData("-a", "--ascii-output")]
    [InlineData("-S", "--sort-keys")]
    [InlineData("-e", "--exit-status")]
    [InlineData("-r", "--raw-output")]
    [InlineData("-j", "--join-output")]
    [InlineData("-M", "--monochrome-output")]
    [InlineData("-b", "--binary")]
    [InlineData("-c", "--compact-output")]
    public async Task Jq_MixedOptionAliasesAreIdempotent(string shortOption, string longOption)
    {
        foreach (string[] options in new[] { new[] { shortOption, longOption }, new[] { longOption, shortOption } })
        {
            var host = new MockFileSystem();
            var root = new BenchmarkMemoryBudget(64 * 1024);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ["-n", .. options, "-c", "1"])));
            Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
            Assert.Equal(shortOption == "-j" ? "1" : "1\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, root.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("--args", "item", "[\"item\"]\n", true)]
    [InlineData("--args", "item", "[\"item\"]\n", false)]
    [InlineData("--jsonargs", "1", "[1]\n", true)]
    [InlineData("--jsonargs", "1", "[1]\n", false)]
    public async Task Jq_GroupedFromFileKeepsArgumentModes(string mode, string argument, string expected, bool modeFirst)
    {
        var host = new MockFileSystem();
        host.AddFile("/filter", "$ARGS.positional");
        var root = new BenchmarkMemoryBudget(64 * 1024);
        string[] args = modeFirst ? ["-n", mode, "-cf", "/filter", argument] : ["-ncf", "/filter", mode, argument];
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", args)));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FromFileAllowsOptionsBeforeItsProgramAndNegativeFilenames()
    {
        var host = new MockFileSystem();
        host.AddFile("/-1", "42");
        var (status, output, error) = await RunCliAsync(host, "-f", "-n", "-1");
        Assert.Equal(0, status);
        Assert.Equal("42\n", output);
        Assert.Empty(error);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("--build-configuration")]
    public async Task Jq_InformationOptionsFinishBeforeLaterInvalidArguments(string option)
    {
        var host = new MockFileSystem();
        var (status, output, error) = await RunCliAsync(host, option, "--argjson", "value", "{", "--unknown");
        Assert.Equal(0, status);
        Assert.NotEmpty(output);
        Assert.Empty(error);
        Assert.Equal(0, host.ReadBytesCallCount);
    }
}
