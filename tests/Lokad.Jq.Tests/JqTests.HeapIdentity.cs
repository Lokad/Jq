using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("walk(.)")]
    [InlineData("fromstream(tostream)")]
    [InlineData("reduce .[] as $x ([];. + [$x])")]
    [InlineData("foreach .[] as $x ([];. + [$x])")]
    public async Task Jq_EqualFreshRootsCannotBecomeUpdateTargets(string source)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "[1]");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "\"ready\", ((" + source + ") |= 7)", "/input")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error (at /input:1): Invalid path expression with result [1]\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_PathIdentityDoesNotTraverseDistinctDeepTrees()
    {
        JsonNode? left = null;
        JsonNode? right = null;
        for (int depth = 0; depth < 5000; depth++)
        {
            left = new JsonArray(left);
            right = new JsonArray(right);
        }
        Assert.True(JqFilter.PathIntact(left, left));
        Assert.False(JqFilter.PathIntact(left, right));
        Assert.False(JqFilter.PathIntact(JsonValue.Create("same"), JsonValue.Create("same")));
    }

    [Fact]
    public void Jq_PathIdentityPreservesImmediateRepresentations()
    {
        Assert.True(JqFilter.PathIntact(null, null));
        Assert.True(JqFilter.PathIntact(JsonValue.Create(true), JsonValue.Create(true)));
        Assert.False(JqFilter.PathIntact(JsonValue.Create(true), JsonValue.Create(1)));
        Assert.True(JqFilter.PathIntact(JsonValue.Create(1L), JsonValue.Create(1.0)));
        Assert.False(JqFilter.PathIntact(JsonValue.Create(0L), JsonValue.Create(-0.0)));
        Assert.True(JqFilter.PathIntact(JsonValue.Create(double.NaN), JsonValue.Create(double.NaN)));
        double otherNaN = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(double.NaN) ^ long.MinValue);
        Assert.False(JqFilter.PathIntact(JsonValue.Create(double.NaN), JsonValue.Create(otherNaN)));
    }
}
