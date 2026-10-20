using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("try @csv catch \"caught\"")]
    [InlineData("@csv?")]
    public async Task Jq_CsvExpansionRefusalPreservesPrefixAndCleanup(string operation)
    {
        const long limit = 1024 * 1024;
        var root = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        host.AddFile("/input", new JsonArray(new string('"', 10000)).ToJsonString());
        host.AddFile("/later", "[]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"ready\", (" + operation + ")", "/input", "/later")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(limit - root.ReservedBytes - 4096, out pressure));
        };
        Assert.Equal(5, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_CsvRefusalDoesNotAllocateEscapedCopy()
    {
        JsonNode input = new JsonArray(new string('"', 100000));
        var root = new BenchmarkMemoryBudget(2);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        for (int index = 0; index < 2; index++)
            Assert.True(Assert.Throws<JqQuotaException>(() => runtime.Format("csv", input)).IsMemoryAdmission);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var failure = Assert.Throws<JqQuotaException>(() => runtime.Format("csv", input));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(failure.IsMemoryAdmission);
        Assert.InRange(allocated, 0, 32 * 1024);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[]", "\n")]
    [InlineData("[null,\"\",true,false,1.5]", ",\"\",true,false,1.5\n")]
    [InlineData("[\"a,b\",\"c\\\"d\",\"é🚀\",\"\\n\"]", "\"a,b\",\"c\"\"d\",\"é🚀\",\"\n\"\n")]
    public async Task Jq_CsvExpansionPreservesExactFields(string input, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "@csv")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
