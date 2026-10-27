using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("match(\"x\";\"g\")", 1024 * 1024)]
    [InlineData("scan(\"(x)\")", 128 * 1024)]
    [InlineData("splits(\"x\")", 128 * 1024)]
    [InlineData("gsub(\"x\"; range(10000) | tostring)", 1024 * 1024)]
    public async Task Jq_RegexRetainedConstructionRefusesBeforePublishingResults(string operation, long allowance)
    {
        var memory = new BenchmarkMemoryBudget(allowance);
        var host = new MockFileSystem();
        host.AddFile("/first", "\"" + new string('x', 4000) + "\"\n");
        host.AddFile("/later", "\"later\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", (try (" + operation + " | empty) catch \"caught\")?, \"after\"", "/first", "/later")));

        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.True(memory.TryReserve(allowance, out var reusable));
        reusable.Dispose();
    }

    [Theory]
    [InlineData("match(\"x\";\"g\")", 4000)]
    [InlineData("scan(\"(x)\")", 4000)]
    [InlineData("splits(\"x\")", 4001)]
    public async Task Jq_AdmittedRegexConstructionKeepsAllResults(string operation, int count)
    {
        var root = new BenchmarkMemoryBudget(32 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        host.SetStandardInput("\"" + new string('x', 4000) + "\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[" + operation + "] | length")));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(count + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations, 1, 20);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_NestedSubstitutionsDiscardOnlyCompletedCaptureAndReplacementDemand()
    {
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        host.SetStandardInput("\"" + new string('x', 10000) + "\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "gsub(\"(?<v>x)\"; .v | gsub(\"x\"; \"\"))")));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 100);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_SubstitutionKeepsEscapingReplacementPayloadCharged()
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        host.BeforeByteAppend = () => Assert.True(memory.ReservedBytes >= 2L * 8192);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "try (\"x\" | gsub(\"(?<v>x)\"; .v | error(. + (\"y\" * 8191)))) catch length")));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("8192\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("splits(\"x\")", "[\"é\\ud83d\\ude00\",\"中\",\"\"]\n")]
    [InlineData("splits(\"\")", "[\"\",\"é\",\"\\ud83d\\ude00\",\"x\",\"中\",\"x\",\"\"]\n")]
    public async Task Jq_RegexSplitsAdvanceByScalarWithoutAnInputSizedOffsetTable(string operation, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\"é😀x中x\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "[" + operation + "]")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
