using Lokad.Jq.Benchmarking;
namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("test(\"x\" * 17000)", "false\n")]
    [InlineData("match(\"x\" * 17000)", "")]
    [InlineData("gsub(\"x\" * 17000; \"y\")", "\"x\"\n")]
    public async Task Jq_RegexProviderCompilesLargeLiteralPatterns(string expression, string expected)
    {
        var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "\"x\" | " + expression)));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("test(\"(?<r>a(?&r)?b)\")", "true\n")]
    [InlineData("match(\"(?<r>a(?&r)?b)\") | .length", "600\n")]
    public async Task Jq_RecursiveRegexUsesProviderExecutionSettings(string expression, string expected)
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "(\"a\" * 300 + \"b\" * 300) | " + expression)));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("compile", "test(\"x\")")]
    [InlineData("boolean", "test(\"x\")")]
    [InlineData("detailed", "match(\"x\")")]
    public async Task Jq_ProviderResourceExhaustionUnwindsOwnedInputs(string stage, string expression)
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        int failures = 0;
        var engine = new ObservedRegexEngine((operation, _, _) =>
        {
            if (operation != stage) return;
            failures++;
            throw new JqRegexResourceException("provider resource exhausted", false);
        });
        var host = new MockFileSystem();
        host.AddFile("/first", "\"x\"\n");
        host.AddFile("/later", "\"later\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", (try (" + expression + ") catch \"caught\")?, \"after\"", "/first", "/later")));

        Assert.Equal(5, await command.ExecuteAsync(host, engine, memory, CancellationToken.None));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: provider resource exhausted\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, failures);
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
