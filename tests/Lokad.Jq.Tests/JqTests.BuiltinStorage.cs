using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_BuiltinMetadataIsStableAndReadOnly()
    {
        IReadOnlyList<string> names = JqBuiltinRegistry.ListAll();
        Assert.Same(names, JqBuiltinRegistry.ListAll());
        Assert.Equal(223, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(names)[0] = "changed");
    }

    [Fact]
    public async Task Jq_BuiltinResultsRemainIndependentWithOneAdmissionPerResult()
    {
        using var compiler = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None));
        JqFilter filter = new JqParser("builtins", JqProgramSource.Inline, compiler.RootEnvironment, compiler.Budget).Parse();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var counted = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None)))
        {
            var first = Assert.IsType<JsonArray>(await filter.Evaluate(null, context, context.RootEnvironment).SingleAsync());
            string original = first[0]?.GetValue<string>() ?? throw new InvalidOperationException();
            first[0] = "changed";
            var second = Assert.IsType<JsonArray>(await filter.Evaluate(null, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(original, second[0]?.GetValue<string>());
            Assert.Equal(original, JqBuiltinRegistry.ListAll()[0]);
            Assert.NotSame(first, second);
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 2);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_BuiltinResultAdmissionRemainsTerminalInsideCatch()
    {
        var root = new BenchmarkMemoryBudget(16 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "try (builtins | length) catch \"caught\"")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_BuiltinResultsObserveCancellationBeforeAndAfterAdmission(bool duringAdmission)
    {
        using var compiler = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None));
        JqFilter filter = new JqParser("builtins", JqProgramSource.Inline, compiler.RootEnvironment, compiler.Budget).Parse();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        if (!duringAdmission) cancellation.Cancel();
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token)))
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await filter.Evaluate(null, context, context.RootEnvironment).SingleAsync());
        Assert.Equal(0, root.ReservedBytes);
    }
}
