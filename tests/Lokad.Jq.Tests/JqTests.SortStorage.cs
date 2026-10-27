using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_SortingBorrowsItsSourceAndAdmitsOnlyNecessaryWorkspace(bool unique)
    {
        var input = new JsonArray();
        for (int index = 0; index < 10000; index++) input.Add((JsonNode?)null);
        var root = new BenchmarkMemoryBudget(600 * 1024);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            var output = Assert.IsType<JsonArray>(unique ? runtime.UniqueArray(input) : runtime.SortArray(input));
            Assert.Equal(unique ? 1 : 10000, output.Count);
            Assert.All(output, Assert.Null);
            output[0] = 1;
            Assert.Null(input[0]);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_SortingPreservesStableNumericTiesAndTheirStorage()
    {
        var input = new JsonArray(JsonValue.Create(-0.0), JsonValue.Create(0.0), JsonValue.Create(0L));
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        JsonArray output = runtime.SortArray(input);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(output[0]?.GetValue<double>() ?? 1));
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.0), BitConverter.DoubleToInt64Bits(output[1]?.GetValue<double>() ?? 1));
        Assert.Equal(0L, output[2]?.GetValue<long>());
        var unique = Assert.IsType<JsonArray>(runtime.UniqueArray(input));
        Assert.Single(unique);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(unique[0]?.GetValue<double>() ?? 1));
    }

    [Fact]
    public void Jq_UniqueKeepsEveryNanAndItsEstablishedOrdering()
    {
        var input = new JsonArray(JsonValue.Create(double.NaN), null, JsonValue.Create(double.NaN), JsonValue.Create(false), JsonValue.Create(0));
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
        var output = Assert.IsType<JsonArray>(new JqRuntime(budget).UniqueArray(input));
        Assert.Equal(5, output.Count);
        Assert.Null(output[0]);
        Assert.True(double.IsNaN(output[1]?.GetValue<double>() ?? 0));
        Assert.True(double.IsNaN(output[2]?.GetValue<double>() ?? 0));
        Assert.False(output[3]?.GetValue<bool>() ?? true);
        Assert.Equal(0, output[4]?.GetValue<int>());
    }
}
