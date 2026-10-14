using System.Globalization;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("try .[0] = 9 catch \"caught\"")]
    [InlineData("try sort_by(.) catch \"caught\"")]
    [InlineData("try group_by(.) catch \"caught\"")]
    [InlineData("try map(.) catch \"caught\"")]
    public async Task Jq_GovernedCollectionAdmissionPreservesPrefixUnderCompetingDemand(string operation)
    {
        const long limit = 1024 * 1024;
        var memory = new TestMemoryBudget(limit);
        var host = new MockFileSystem();
        string input = "[" + string.Join(',', Enumerable.Range(0, 1000)
            .Select(number => number.ToString(CultureInfo.InvariantCulture))) + "]";
        host.AddFile("/input", input);
        host.AddFile("/later", input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "., (" + operation + ")", "/input", "/later")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null)
                Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 4096, out pressure));
        };

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(input + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("try indices(\"x\") catch \"caught\"")]
    [InlineData("try trim catch \"caught\"")]
    [InlineData("try getpath([{start: 0, end: 8000}]) catch \"caught\"")]
    public async Task Jq_GovernedUnicodeScratchAdmissionPrecedesLargeLists(string operation)
    {
        const long limit = 1024 * 1024;
        var memory = new TestMemoryBudget(limit);
        var host = new MockFileSystem();
        string value = new('x', 10000);
        host.SetStandardInput("\"" + value + "\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "., (" + operation + ")")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null)
                Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 4096, out pressure));
        };

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(value + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("sort_by(.)", "[1,2,3]\n")]
    [InlineData("group_by(. % 2)", "[[2],[3,1]]\n")]
    [InlineData(".[0] = 9", "[9,1,2]\n")]
    [InlineData("delpaths([[1]])", "[3,2]\n")]
    [InlineData("map(. + 1)", "[4,2,3]\n")]
    [InlineData("getpath([{start: 1, end: 3}])", "[1,2]\n")]
    public async Task Jq_GovernedCollectionCopiesRetainExactValuesAndOrdering(string filter, string expected)
    {
        var memory = new TestMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("[3,1,2]\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
