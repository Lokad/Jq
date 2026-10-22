using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("test(\"a\";\"g\")")]
    [InlineData("_match_impl(\"a\";\"g\";true)")]
    public async Task Jq_BooleanRegexUsesOnePredicateWithoutDetailedCaptures(string operation)
    {
        int boolean = 0;
        var engine = new ObservedRegexEngine((stage, _, _) =>
        {
            if (stage == "detailed") Assert.Fail("Boolean matching must not request captures.");
            if (stage == "boolean") boolean++;
        });
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--arg", "text", new string('a', 1000),
            "$text | " + operation)));
        Assert.Equal(0, await command.ExecuteAsync(host, engine, memory, CancellationToken.None));
        Assert.Equal("true\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, boolean);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InternalRegexModeIsExactTrueAndRetainsArgumentOrder()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "\"aaa\" | _match_impl((\"pattern\"|debug|\"a\");(\"flags\"|debug|\"g\");(\"mode\"|debug|1)) | length")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("3\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",\"mode\"]\n[\"DEBUG:\",\"flags\"]\n[\"DEBUG:\",\"pattern\"]\n", host.GetOutput(JqFileDescriptor.StdErr));
    }
}
