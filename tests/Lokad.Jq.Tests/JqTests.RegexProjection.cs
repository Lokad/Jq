using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("scan(\"a\") | empty")]
    [InlineData("splits(\"a\") | empty")]
    public async Task Jq_RegexStringProjectionsAvoidMatchMetadataWithinSharedAdmission(string operation)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--arg", "text", new string('a', 1000), "$text | " + operation)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("match(\"a\";\"g\")", false)]
    [InlineData("scan(\"a\")", false)]
    [InlineData("splits(\"a\")", false)]
    [InlineData("split(\"a\";\"\") | .[]", false)]
    [InlineData("match(\"a\";\"g\")", true)]
    [InlineData("scan(\"a\")", true)]
    [InlineData("splits(\"a\")", true)]
    [InlineData("split(\"a\";\"\") | .[]", true)]
    public async Task Jq_RegexProjectionsKeepEagerFailureAndCancellationBeforeFirstResult(string operation, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        int detailed = 0;
        var engine = new ObservedRegexEngine((stage, _, _) =>
        {
            if (stage != "detailed" || ++detailed != 3) return;
            if (cancel) cancellation.Cancel();
            else throw new JqRegexResourceException("provider exhausted", false);
        });
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "\"prefix\", (\"aaaa\" | first(" + operation + ")), \"after\"")));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, engine, memory, cancellation.Token));
        else Assert.Equal(5, await command.ExecuteAsync(host, engine, memory, cancellation.Token));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(cancel ? "" : "jq: provider exhausted\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(3, detailed);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("é🚀ab", "[match(\"(?<=(é🚀))(a)(b)?\") | [.offset,.length,(.captures|map([.offset,.length,.string]))]]", "[[2,2,[[0,2,\"\\u00e9\\ud83d\\ude80\"],[2,1,\"a\"],[3,1,\"b\"]]]]\n")]
    [InlineData("🚀ab", "[match(\"(?=(a(b)))a\") | [.offset,.length,(.captures|map([.offset,.length,.string]))]]", "[[1,1,[[1,2,\"ab\"],[2,1,\"b\"]]]]\n")]
    [InlineData("é🚀a", "[scan(\"(é🚀)|(a)\")]", "[[\"\\u00e9\\ud83d\\ude80\",null],[null,\"a\"]]\n")]
    [InlineData("é🚀a🚀", "[splits(\"🚀\")]", "[\"\\u00e9\",\"a\",\"\"]\n")]
    [InlineData("é🚀", "[splits(\"\")]", "[\"\",\"\\u00e9\",\"\\ud83d\\ude80\",\"\"]\n")]
    [InlineData("é🚀", "[match(\".\";\"g\") | [.offset,.length]]", "[[0,1],[1,1]]\n")]
    public async Task Jq_RegexProjectionPreservesUnicodeAndNonmonotonicCapturePositions(string input, string filter, string expected)
    {
        var host = new MockFileSystem(); host.SetStandardInput(System.Text.Json.JsonSerializer.Serialize(input));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-ac", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("test(\"a\";\"g\")")]
    [InlineData("_match_impl(\"a\";\"g\";true)")]
    public async Task Jq_BooleanRegexUsesOnePredicateWithoutDetailedCaptures(string operation)
    {
        int boolean = 0;
        var engine = new ObservedRegexEngine((stage, _, _) =>
        {
            if (stage == "detailed") Assert.Fail("Boolean matching must not request captures.");
            if (stage == "boolean") boolean++;
        });
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--arg", "text", new string('a', 1000),
            "$text | " + operation)));
        Assert.Equal(0, await command.ExecuteAsync(host, engine, memory, CancellationToken.None));
        Assert.Equal("true\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, boolean);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InternalRegexModeIsExactTrueAndRetainsArgumentOrder()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "\"aaa\" | _match_impl((\"pattern\"|debug|\"a\");(\"flags\"|debug|\"g\");(\"mode\"|debug|1)) | length")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("3\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",\"mode\"]\n[\"DEBUG:\",\"flags\"]\n[\"DEBUG:\",\"pattern\"]\n", host.GetOutput(JqFileDescriptor.StdErr));
    }
}
