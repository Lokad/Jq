using System.Text;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("try (\"x\" * 100000) catch \"caught\"")]
    [InlineData("(\"x\" * 100000)?")]
    [InlineData("try [range(10000)] catch \"caught\"")]
    [InlineData("try {values: [range(10000)]} catch \"caught\"")]
    public async Task Jq_GovernedValueAdmissionPrecedesExpansionAndCannotBeCaught(string filter)
    {
        var memory = new TestMemoryBudget(16 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.InRange(memory.PeakBytes, 1, 16 * 1024);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("raw")]
    [InlineData("seq")]
    [InlineData("stream")]
    public async Task Jq_GovernedInputValuesReuseAllowanceAcrossOuterRecords(string mode)
    {
        var memory = new TestMemoryBudget(64 * 1024);
        string value = new('x', 4096);
        const int records = 300;
        string input = mode switch
        {
            "raw" => string.Concat(Enumerable.Repeat(value + "\n", records)),
            "seq" => string.Concat(Enumerable.Repeat("\u001e\"" + value + "\"\n", records)),
            "stream" => "[" + string.Join(',', Enumerable.Repeat("\"" + value + "\"", records)) + "]",
            _ => string.Concat(Enumerable.Repeat("\"" + value + "\"\n", records)),
        };
        string[] arguments = mode switch
        {
            "raw" => ["-R", "-r", "."],
            "seq" => ["--seq", "-r", "."],
            "stream" => ["--stream", "-r", "select(length == 2) | .[1]"],
            _ => ["-r", "."],
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        var host = new MockFileSystem();
        host.SetStandardInput(input);

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(value + "\n", records))),
            host.GetOutputBytes(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.InRange(memory.PeakBytes, 8192, 64 * 1024);
    }

    [Fact]
    public async Task Jq_GovernedSlurpRetainsValuesInsteadOfRefundingBorrowedRecords()
    {
        var memory = new TestMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("\"" + new string('x', 4096) + "\"\n", 100)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-s", "length")));

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.True(host.ReadBytesCallCount < 10);
    }

    [Fact]
    public async Task Jq_GovernedVariableAliasesRemainChargedDuringNestedInput()
    {
        var memory = new TestMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        string value = new('x', 4096);
        host.SetStandardInput("\"" + value + "\"\n\"" + value + "\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", ". as $first | input | $first + .")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(value + value + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.True(memory.PeakBytes >= 32 * 1024);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedModuleCompilationStaysChargedAcrossRecordRefunds()
    {
        var memory = new TestMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/module.jq", "def value: \"" + new string('x', 4096) + "\";");
        host.SetStandardInput(string.Concat(Enumerable.Repeat("null\n", 100)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-L", "/", "\"module\" | modulemeta | .defs[0]")));
        var charges = new List<long>();
        host.BeforeByteAppend = () => charges.Add(memory.ReservedBytes);

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("\"value/0\"\n", 100)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(100, charges.Count);
        Assert.All(charges, bytes => Assert.True(bytes > 40 * 1024));
        Assert.True(charges.Max() - charges.Min() < 4096);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedDiagnosticsReleaseSerializationAndQueueStorageBetweenRecords()
    {
        var memory = new TestMemoryBudget(96 * 1024);
        var host = new MockFileSystem();
        string value = new('x', 4096);
        host.SetStandardInput(string.Concat(Enumerable.Repeat("\"" + value + "\"\n", 300)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "debug | stderr")));

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat(value + "\n", 300)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(string.Concat(Enumerable.Repeat("[\"DEBUG:\",\"" + value + "\"]\n" + value, 300)),
            host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.InRange(memory.PeakBytes, 20 * 1024, 96 * 1024);
    }

    [Theory]
    [InlineData("seq")]
    [InlineData("stream")]
    public async Task Jq_GovernedCursorPayloadStaysChargedThroughTerminalDiagnostic(string mode)
    {
        var memory = new TestMemoryBudget(96 * 1024);
        var host = new MockFileSystem();
        string value = new('x', 4096);
        host.AddFile("/input", (mode == "seq" ? "\u001e" : "") + "\"" + value + "\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq",
            mode == "seq" ? "--seq" : "--stream",
            mode == "seq" ? "halt_error(7)" : ".[1] | halt_error(7)", "/input")));
        host.BeforeByteAppend = () =>
        {
            Assert.Single(host.ClosedDescriptors);
            Assert.True(memory.ReservedBytes >= 2 * value.Length);
        };

        Assert.Equal(7, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(value, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
