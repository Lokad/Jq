using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("string")]
    [InlineData("array")]
    [InlineData("key")]
    [InlineData("suffix")]
    [InlineData("unicode")]
    [InlineData("escape")]
    public void Jq_OperandPreviewUsesBoundedStorage(string kind)
    {
        string large = new('x', 100000);
        JqValue value = kind switch
        {
            "array" => new JqArray(NativeFixtures.Create(large)),
            "key" => new JqObject(NativeFixtures.Budget()) { [large] = 1 },
            "suffix" => new JqObject(NativeFixtures.Budget()) { ["a"] = NativeFixtures.Create(new string('p', 32)), ["z"] = NativeFixtures.Create(large )},
            "unicode" => NativeFixtures.Create(new string('\u20ac', 100000)),
            "escape" => NativeFixtures.Create(new string('\n', 100000)),
            _ => NativeFixtures.Create(large)
        };
        string expected = kind switch
        {
            "array" => "[\"" + new string('x', 23) + "...]",
            "key" => "{\"" + new string('x', 23) + "...}",
            "suffix" => "{\"a\":\"" + new string('p', 19) + "...}",
            "unicode" => "\"" + new string('\u20ac', 8) + "...\"",
            "escape" => "\"" + string.Concat(Enumerable.Repeat("\\n", 12)) + "...\"",
            _ => "\"" + new string('x', 24) + "...\""
        };
        var root = new BenchmarkMemoryBudget(4096);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
            Assert.Equal(expected, new JqRuntime(budget).TruncatedDump(value));
        Assert.InRange(root.PeakBytes, 1, 4096);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("null", "null")]
    [InlineData("123", "123")]
    [InlineData("[1,true,null]", "[1,true,null]")]
    [InlineData("{\"a\":[1]}", "{\"a\":[1]}")]
    [InlineData("\"small\"", "\"small\"")]
    [InlineData("[]", "[]")]
    [InlineData("{}", "{}")]
    public void Jq_OperandPreviewKeepsCompleteShortValues(string json, string expected)
    {
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
        Assert.Equal(expected, new JqRuntime(budget).TruncatedDump(NativeFixtures.Parse(json)));
    }

    [Theory]
    [InlineData("try ((\"x\" * 100000) + 1) catch \"caught\"")]
    [InlineData("try ({a:(\"p\" * 32),z:(\"x\" * 100000)} + 1) catch \"caught\"")]
    public async Task Jq_BoundedPreviewPreservesCatchUnderTightAdmission(string filter)
    {
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("\"caught\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_OperandPreviewPreservesCancellationBeforeAndDuringAdmission(bool duringAdmission)
    {
        var root = new BenchmarkMemoryBudget(4096);
        using var cancellation = new CancellationTokenSource();
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        if (!duringAdmission) cancellation.Cancel();
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token))
            Assert.Throws<OperationCanceledException>(() => new JqRuntime(budget).TruncatedDump(new JqArray(1)));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_OperandPreviewDoesNotRecoverFromAdmissionRefusal()
    {
        var root = new BenchmarkMemoryBudget(0);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
            Assert.True(Assert.Throws<JqQuotaException>(() => new JqRuntime(budget).TruncatedDump(NativeFixtures.Create("x"))).IsMemoryAdmission);
        Assert.Equal(0, root.ReservedBytes);
    }
}
