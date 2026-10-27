using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    // Expected results checked against jq 1.8.2.
    [Theory]
    [InlineData("[path(label $out | (.a,break $out))]", "[[\"a\"]]\n")]
    [InlineData("[label $out | path((.a,break $out))]", "[[\"a\"]]\n")]
    [InlineData("[path(label $out | (label $inner | (.a,break $inner),.b))]", "[[\"a\"]]\n")]
    [InlineData("[path((.a,error(\"late\"))?)]", "[[\"a\"]]\n")]
    [InlineData("try path((.a | error)?) catch .", "")]
    [InlineData("try [path((.a,error(\"late\"))?)] catch .", "[[\"a\"]]\n")]
    [InlineData("try path(try error(\"different\") catch .) catch .", "\"Invalid path expression with result \\\"different\\\"\"\n")]
    [InlineData("try ((try error(\"different\") catch .) |= 99) catch .", "\"Invalid path expression with result \\\"different\\\"\"\n")]
    [InlineData("[try (1,error(\"x\"),2) catch .]", "[1,\"x\"]\n")]
    public async Task Jq_PathControlPreservesBoundaries(string source, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":1,\"b\":2}\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("[]")]
    [InlineData("[1,2]")]
    [InlineData("{\"a\":[1]}")]
    public async Task Jq_CaughtErrorsKeepBorrowedIdentity(string input)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            ". as $saved | (try error catch .) | path($saved)")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("[]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_CaughtErrorBorrowsDeepPayloadWithoutAdmissionAndStillCancels()
    {
        JsonNode? payload = new JsonArray();
        for (int index = 0; index < 100; index++) payload = new JsonArray(payload);
        var root = new BenchmarkMemoryBudget(0);
        using var cancellation = new CancellationTokenSource();
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(Utf8RegexAdapter.Instance, root, cancellation.Token)))
        {
            var filter = new TryFilter(new FunctionFilter("error", []), new IdentityFilter());
            Assert.Same(payload, await filter.Evaluate(payload, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(0, root.ReservedBytes);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await filter.Evaluate(payload, context, context.RootEnvironment).ToArrayAsync());
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_CatchKeepsPayloadAcrossExplicitInputAdvancementAndIndependentUpdates()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":[1]}\n{\"next\":2}\n");
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "try error catch (. as $saved | input, (.a[0] = 9), $saved)")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("{\"next\":2}\n{\"a\":[9]}\n{\"a\":[1]}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try error catch length")]
    [InlineData("min_by(.)")]
    [InlineData("max_by(.)")]
    [InlineData("keys | length")]
    [InlineData("keys_unsorted | length")]
    [InlineData("length | floor")]
    [InlineData("[path(label $out | (.a,break $out))] | length")]
    [InlineData("[path((.a,error(\"late\"))?)] | length")]
    public async Task Jq_ErrorAndPathControlKeepLongStreamsBounded(string source)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        bool extrema = source is "min_by(.)" or "max_by(.)";
        host.AddFile("/input", string.Concat(Enumerable.Repeat(extrema ? "[1,1]\n" : "{\"a\":1}\n", 2500)));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source, "/input")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("1\n", 2500)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
