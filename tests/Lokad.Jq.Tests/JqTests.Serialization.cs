using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_SortedNonFiniteOutputFitsOneTreeCopyAndPreservesBorrowedInput()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var items = new JsonArray(double.NaN, double.PositiveInfinity, double.NegativeInfinity);
        for (int index = 3; index < 1000; index++) items.Add(index);
        var input = new JsonObject { ["z"] = items, ["a"] = "kept" };
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        {
            using var result = new JqRuntime(budget).SerializeUtf8(input, false, null, false, true);
            using var document = JsonDocument.Parse(result.Value);
            Assert.Equal(new[] { "a", "z" }, document.RootElement.EnumerateObject().Select(property => property.Name));
            var output = document.RootElement.GetProperty("z");
            Assert.Equal(1000, output.GetArrayLength());
            Assert.Equal(JsonValueKind.Null, output[0].ValueKind);
            Assert.Equal(double.MaxValue, output[1].GetDouble());
            Assert.Equal(double.MinValue, output[2].GetDouble());
            for (int index = 3; index < 1000; index++) Assert.Equal(index, output[index].GetInt32());
            Assert.True(double.IsNaN(items[0]?.GetValue<double>() ?? 0));
            Assert.True(double.IsPositiveInfinity(items[1]?.GetValue<double>() ?? 0));
            Assert.True(double.IsNegativeInfinity(items[2]?.GetValue<double>() ?? 0));
            Assert.Equal(new[] { "z", "a" }, input.Select(property => property.Key));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_IndentedOutputUsesLfAndPreservesStringCarriageReturns(bool tabs)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"v\":\"a\\r\\nb\",\"items\":[1]}");
        string[] arguments = tabs ? ["--tab", "."] : ["--indent", "2", "."];
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        string indentation = tabs ? "\t" : "  ";
        string expected = "{\n" + indentation + "\"v\": \"a\\r\\nb\",\n"
            + indentation + "\"items\": [\n" + indentation + indentation + "1\n"
            + indentation + "]\n}\n";

        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), host.GetOutputBytes(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public async Task Jq_SerializationPreservesLiteralUnicodeEscapeSpelling(int slashes, bool ascii)
    {
        string json = "\"" + new string('\\', 2 * slashes) + "uABCD\\\"quote\"";
        string expected = "\"" + new string('\\', 2 * slashes)
            + (ascii ? "uABCD\\u0022quote\"\n" : "uABCD\\\"quote\"\n");
        foreach (string filter in new[] { ".", "[.] | tostring | fromjson | .[0]", "tojson | fromjson" })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(json);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ascii ? "-ac" : "-c", filter)));

            Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(Encoding.UTF8.GetBytes(expected), host.GetOutputBytes(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
        }
    }

    [Fact]
    public async Task Jq_SerializationLowercasesRealEscapesBesideLiteralEscapesAndQuotes()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\"\\\\uABCD\\\"\\uABCD\\u001F\\ud83d\\ude80\"");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-ac", ".")));

        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"\\\\uABCD\\u0022\\uabcd\\u001f\\ud83d\\ude80\"\n"u8.ToArray(), host.GetOutputBytes(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
    }
}
