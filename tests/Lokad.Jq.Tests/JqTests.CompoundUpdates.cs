using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{\"a\":null,\"b\":null}", "(.a,.b) //= ([1],[2])", "{\"a\":[1],\"b\":[1]}\n{\"a\":[2],\"b\":[2]}\n", "")]
    [InlineData("{\"a\":1,\"b\":2}", "(.a,.b) += (1,2)", "{\"a\":2,\"b\":3}\n{\"a\":3,\"b\":4}\n", "")]
    [InlineData("{\"a\":1}", ".a //= empty", "", "")]
    [InlineData("{\"a\":null}", ".a //= (1,2)", "{\"a\":1}\n{\"a\":2}\n", "")]
    [InlineData("{\"a\":true}", ".a //= (1|debug)", "{\"a\":true}\n", "[\"DEBUG:\",1]\n")]
    [InlineData("[true,false,null,0,\"\"]", ".[] //= 1", "[true,1,1,0,\"\"]\n", "")]
    [InlineData("{\"a\":\"x,y\"}", ".a /= \",\"", "{\"a\":[\"x\",\"y\"]}\n", "")]
    public async Task Jq_CompoundUpdatesKeepRhsProductsEffectsAndEmptyStreams(string input, string source, string expected, string error)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(".slot //= $value")]
    [InlineData(".slot += $value")]
    public async Task Jq_CompoundUpdatesAdmitOnlyTheRetainedRhsCopy(string source)
    {
        const long allowance = 8 * 1024 * 1024;
        var root = new BenchmarkMemoryBudget(allowance);
        string payload = "[" + string.Join(',', Enumerable.Range(0, 10000)) + "]";
        var host = new MockFileSystem();
        host.AddFile("/input", "{\"slot\":null}\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--argjson", "value", payload,
            "\"ready\", ((" + source + ") | .slot | length)", "/input")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(allowance - root.ReservedBytes - 1024 * 1024, out pressure));
        };
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("\"ready\"\n10000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, root.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, root.ReservedBytes);
    }
}
