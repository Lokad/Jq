using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_CaughtPredicateErrorsDoNotOutliveOuterRecordOwners()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("0\n", 10000)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "try all(1; error(\"x\" * 128)) catch empty")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("all", "==", "true\n")]
    [InlineData("any", "!=", "false\n")]
    public async Task Jq_PredicateTemporaryStorageStaysBounded(string operation, string comparison, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        string filter = operation + "(range(100000); (\"x\" * 128 | length) " + comparison + " 128)";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 40);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_PredicateOwnersDoNotRetainSuccessfulInvocationHistory()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "range(10000) | all(range(2); \"x\" * 128 | length == 128) | empty")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_PredicateStorageKeepsBorrowedAliasesAndCollectedAnswersCharged()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "(\"q\" * 10000) as $kept | all(range(1000); {a:$kept,b:(\"x\" * 128)} | .a == $kept), ($kept | length)")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("true\n10000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);

        var collected = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "[range(10000) | all(1; true)] | length")));
        var refused = new MockFileSystem();
        Assert.Equal(5, await collected.ExecuteAsync(refused, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(refused.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", refused.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("any")]
    public async Task Jq_PredicateAdmissionRefusalIsTerminal(string operation)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "\"prefix\", (try " + operation + "(range(1000); \"x\" * 1000000) catch \"caught\"), \"later\"")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("any")]
    public async Task Jq_EscapingPredicateErrorKeepsItsPayloadAdmission(string operation)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None);
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), budget);
        var parser = new JqParser(operation + "(1; error(\"x\" * 8192))", JqProgramSource.Inline, context.RootEnvironment, budget);
        var filter = parser.Parse();
        long compilation = memory.ReservedBytes;
        var error = await Assert.ThrowsAsync<JqErrorException>(async () => await filter.Evaluate(null, context, context.RootEnvironment).ToArrayAsync());
        Assert.True(memory.ReservedBytes >= compilation + 2 * 8192);
        Assert.Equal(new string('x', 8192), error.Payload?.GetValue<string>());
        budget.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }
}
