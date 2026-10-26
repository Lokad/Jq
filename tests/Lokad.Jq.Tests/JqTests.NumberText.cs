using System.Globalization;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"-0\"|tonumber|copysign(1;.)", "-1\n")]
    [InlineData("(-1*0)|tostring|tonumber|copysign(1;.)", "-1\n")]
    [InlineData("\"-00\"|tonumber|copysign(1;.)", "-1\n")]
    [InlineData("\"-0\"|tonumber|tojson", "\"-0\"\n")]
    [InlineData("[\"0\",\"+00\",\"-0.0\",\"-0e0\",\"-.0\"]|map(tonumber|copysign(1;.))", "[1,1,-1,-1,-1]\n")]
    public async Task Jq_NumberTextPreservesZeroSigns(string filter, string expected)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("\0-0")]
    [InlineData("-\0" )]
    [InlineData("12\0.3")]
    [InlineData("1e\0")]
    [InlineData("NaN\0")]
    [InlineData("\0inf")]
    [InlineData("+")]
    [InlineData("1.")]
    [InlineData("1e+")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    public void Jq_NumberTextRejectsInvalidGrammarWithoutFallbackAcceptance(string text)
    {
        using var budget = new JqBudget(CancellationToken.None);
        Assert.Throws<JqException>(() => new JqRuntime(budget).ToJsonNumber(JsonValue.Create(text)));
    }

    [Fact]
    public void Jq_NumberTextKeepsInvariantGrammarAndExactIntegerStorage()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var budget = new JqBudget(CancellationToken.None);
            var runtime = new JqRuntime(budget);
            Assert.Equal(0.5, JqRuntime.Number(runtime.ToJsonNumber(JsonValue.Create("+.5"))));
            Assert.Equal(100, JqRuntime.Number(runtime.ToJsonNumber(JsonValue.Create("001E2"))));
            var integer = Assert.IsAssignableFrom<JsonValue>(runtime.ToJsonNumber(JsonValue.Create("9007199254740993")));
            Assert.True(integer.TryGetValue<long>(out long exact));
            Assert.Equal(9007199254740993, exact);
            Assert.True(double.IsNaN(JqRuntime.Number(runtime.ToJsonNumber(JsonValue.Create("+NaN")))));
            Assert.Equal(double.NegativeInfinity, JqRuntime.Number(runtime.ToJsonNumber(JsonValue.Create("-INFINITY"))));
            Assert.Throws<JqException>(() => runtime.ToJsonNumber(JsonValue.Create("1,5")));
            JsonNode numeric = JsonValue.Create(-0.0);
            Assert.Same(numeric, runtime.ToJsonNumber(numeric));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("whole")]
    [InlineData("fraction")]
    [InlineData("exponent")]
    [InlineData("numeric")]
    public void Jq_NumberTextChecksCancellationBeforeParsingOrReturningExistingValues(string kind)
    {
        string zeros = new('0', 1024 * 1024);
        JsonNode input = kind == "numeric" ? JsonValue.Create(-0.0)
            : JsonValue.Create(kind == "fraction" ? "0." + zeros : kind == "exponent" ? "0e" + zeros : zeros);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var memory = new BenchmarkMemoryBudget(0);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => new JqRuntime(budget).ToJsonNumber(input));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("closed")]
    [InlineData("cancelled")]
    public async Task Jq_NumberTextStreamsKeepOwnershipAndStopAtHostBoundaries(string mode)
    {
        const int records = 2500;
        using var cancellation = new CancellationTokenSource();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = mode != "closed" };
        host.AddFile("/input", string.Concat(Enumerable.Repeat("\"-00\"\n", records)));
        host.AddFile("/later", "\"1\"\n");
        if (mode == "cancelled") host.BeforeByteAppend = () => { if (host.AppendCallCount == 2) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "tonumber|copysign(1;.)", "/input", "/later")));
        if (mode == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        else Assert.Equal(0, await command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal(mode == "stream" ? string.Concat(Enumerable.Repeat("-1\n", records)) + "1\n"
            : mode == "closed" ? "-1\n" : "-1\n-1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(mode == "stream" ? 2 : 1, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
