using Lokad.Jq.Benchmarking;
using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(65534)]
    [InlineData(65535)]
    [InlineData(65536)]
    public async Task Jq_Utf8CountsPreservePairsAcrossCooperativeChunks(int prefix)
    {
        string text = new string('x', prefix) + "🚀é";
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        var invocation = BuildInvocation("jq", "-nr", "--arg", "text", text,
            "$text, ($text | utf8bytelength)");
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(text + "\n" + (prefix + 6) + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("--stream")]
    [InlineData("--stream-errors")]
    public async Task Jq_StreamingReadsAdmittedLargeStringTokens(string mode)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("\"" + new string('x', 8388609) + "\"");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", mode, ".[1] | length")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("8388609\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("\"ab\" * 4194305 | length", "8388610\n")]
    [InlineData("(\"x\" * 8388608) + \"y\" | length", "8388609\n")]
    [InlineData("[\"x\" * 4194304, \"y\" * 4194305] | join(\"\") | length", "8388609\n")]
    public async Task Jq_StringConstructionUsesSharedAdmission(string filter, string expected)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_AdmittedLargeInputCountsMixedEscapes(bool raw)
    {
        var memory = new BenchmarkMemoryBudget(96 * 1024 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(raw ? new string('x', 8388609)
            : "\"" + new string('x', 8388603) + "\\u0061é\\n\\\\\\uD83D\\uDE80\"");
        var invocation = raw ? BuildInvocation("jq", "-R", "length") : BuildInvocation("jq", "length");
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(raw ? "8388609\n" : "8388608\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_StringRepresentabilityCheckDoesNotAllocate()
    {
        using var budget = new JqBudget(CancellationToken.None);
        Assert.Equal("string result cannot be represented",
            Assert.Throws<JqQuotaException>(() => budget.ReserveString((long)int.MaxValue + 1)).Message);
    }

    [Fact]
    public void Jq_CanceledReadsOfBorrowedStringsStillPropagate()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(cancellation.Token);
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), budget);
        var node = JsonValue.Create("borrowed");
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new IdentityFilter().Evaluate(node, context, context.RootEnvironment).ToArray());
    }

    [Fact]
    public async Task Jq_SharedRegexEngineSupportsConcurrentCommands()
    {
        var regexEngine = JqUtf8RegexEngine.Instance;
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"x\" | test(\"x\")")));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            var host = new MockFileSystem();
            Assert.Equal(0, await command.ExecuteAsync(host, regexEngine, CancellationToken.None));
            Assert.Equal("true\n", host.GetOutput(JqFileDescriptor.StdOut));
        }));
    }
}
