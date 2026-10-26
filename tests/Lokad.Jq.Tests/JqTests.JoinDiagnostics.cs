using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false, 32768)]
    [InlineData(true, 32768)]
    [InlineData(false, 1048576)]
    [InlineData(true, 1048576)]
    public async Task Jq_JoinDiagnosticsKeepLongAccumulatorErrorsCatchable(bool invalidSeparator, int length)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(3L * length);
        string expression = invalidSeparator ? "[$text,\"y\"]|join(1)" : "[$text,{}]|join(\"\")";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "text", new string('x', length),
            "try (" + expression + ") catch .")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("string (\"xxxxxxxxxxxxxxxxxxxxxxxx...\") and "
            + (invalidSeparator ? "number (1)" : "object ({})") + " cannot be added\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("x", 24)]
    [InlineData("x", 25)]
    [InlineData("é", 13)]
    [InlineData("🚀", 16)]
    [InlineData("\n", 32)]
    [InlineData("\"", 32)]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxx🚀", 2)]
    public async Task Jq_JoinDiagnosticsPreserveExistingPreviewBoundaries(string unit, int count)
    {
        string text = string.Concat(Enumerable.Repeat(unit, count));
        using var budget = new JqBudget(CancellationToken.None);
        string preview = new JqRuntime(budget).TruncatedDump(JsonValue.Create(text));
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "text", text,
            "try ([$text,{}]|join(\"\")) catch .")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("string (" + preview + ") and object ({}) cannot be added\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("closed")]
    [InlineData("cancelled")]
    public async Task Jq_JoinDiagnosticsLongStreamsStopAtHostBoundaries(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem { AppendRemainsOpen = mode != "closed" };
        host.AddFile("/records", string.Concat(Enumerable.Repeat("0\n", 200)));
        if (mode == "cancelled") host.BeforeByteAppend = () => { if (host.AppendCallCount == 2) cancellation.Cancel(); };
        var memory = new BenchmarkMemoryBudget(96 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "--arg", "text", new string('x', 32768),
            "try ([$text,{}]|join(\"\")) catch .", "/records")));
        if (mode == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        else Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        int count = mode == "stream" ? 200 : mode == "closed" ? 1 : 2;
        Assert.Equal(string.Concat(Enumerable.Repeat("string (\"xxxxxxxxxxxxxxxxxxxxxxxx...\") and object ({}) cannot be added\n", count)),
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_JoinDiagnosticsKeepNecessaryAdmissionRefusalTerminal(bool successfulOutput)
    {
        var host = new MockFileSystem();
        host.AddFile("/records", "0\n");
        var memory = new BenchmarkMemoryBudget(successfulOutput ? 96 * 1024 : 32 * 1024);
        string expression = successfulOutput ? "[$text]|join(\"\")" : "[$text,{}]|join(\"\")";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "--arg", "text", new string('x', 32768),
            "\"ready\", (try (" + expression + ") catch \"caught\")", "/records")));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_JoinDiagnosticsDoNotRecoverFromPreviewAdmissionRefusal()
    {
        var input = new JsonArray(JsonValue.Create(new string('x', 16384)), new JsonObject());
        var memory = new BenchmarkMemoryBudget(32768);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        {
            var error = Assert.Throws<JqQuotaException>(() => new JqRuntime(budget).Join(input, JsonValue.Create("")));
            Assert.True(error.IsMemoryAdmission);
            Assert.Equal(32768, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }
}
