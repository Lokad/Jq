using System.Globalization;
using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("try .[0] = 9 catch \"caught\"")]
    [InlineData("try sort_by(.) catch \"caught\"")]
    [InlineData("try group_by(.) catch \"caught\"")]
    [InlineData("try map(.) catch \"caught\"")]
    [InlineData("try . + . catch \"caught\"")]
    [InlineData("try reverse catch \"caught\"")]
    [InlineData("try keys catch \"caught\"")]
    [InlineData("try to_entries catch \"caught\"")]
    [InlineData("try walk(.) catch \"caught\"")]
    [InlineData("try [tostream] catch \"caught\"")]
    [InlineData("try [paths] catch \"caught\"")]
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
    [InlineData("try explode catch \"caught\"")]
    [InlineData("try split(\"\") catch \"caught\"")]
    [InlineData("try ltrimstr(\"x\") catch \"caught\"")]
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

    [Fact]
    public void Jq_GovernedConstantModuleMetadataAdmitsContainerCopies()
    {
        string source = "module {data: [" + string.Join(',', Enumerable.Repeat("1", 100)) + "]}; .";
        using var lexicalBudget = new JqBudget(CancellationToken.None);
        var tokens = Lexer.Tokenize(source, JqProgramSource.Inline, lexicalBudget);
        var memory = new TestMemoryBudget(1024);
        using (var budget = new JqBudget(JqExecutionPolicy.Default, memory, CancellationToken.None))
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), JqProgramSource.Inline, budget))
        {
            var parser = new JqParser(tokens, JqProgramSource.Inline, context.RootEnvironment, budget);
            var failure = Assert.Throws<JqQuotaException>(() => parser.ParseImportsOnly());
            Assert.True(failure.IsMemoryAdmission);
            Assert.InRange(memory.ReservedBytes, 1, 1024);
        }
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
