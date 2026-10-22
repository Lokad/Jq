using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_AddExtendsItsPrivateObjectWithoutCopyingHistory()
    {
        var fragments = new JsonArray();
        for (int index = 0; index < 1024; index++)
            fragments.Add(new JsonObject { ["k" + index] = index });
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var counted = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, counted, CancellationToken.None))
        {
            var result = Assert.IsType<JsonObject>(new JqRuntime(budget).AddValues(fragments));
            Assert.Equal(1024, result.Count);
            for (int index = 0; index < result.Count; index++)
            {
                Assert.Equal(index, result["k" + index]?.GetValue<int>());
                Assert.Equal(index, fragments[index]?["k" + index]?.GetValue<int>());
                Assert.NotSame(result["k" + index], fragments[index]?["k" + index]);
            }
            result["k0"] = -1;
            Assert.Equal(0, fragments[0]?["k0"]?.GetValue<int>());
            Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 100);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_AddBuildsStringsWithoutRepeatedPrefixesAndBorrowsSingleValues()
    {
        JsonNode text = JsonValue.Create("x");
        JsonNode obj = new JsonObject { ["a"] = 1 };
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            Assert.Same(text, runtime.AddValues(new JsonNode?[] { null, text, null }));
            Assert.Same(obj, runtime.AddValues(new JsonNode?[] { obj, null }));
            JsonNode? result = runtime.AddValues(Enumerable.Repeat<JsonNode?>(text, 10000));
            Assert.Equal(new string('x', 10000), result?.GetValue<string>());
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("[{a:1,b:2},null,{c:3,a:4},null,{b:{x:5}}] | add", "{\"a\":4,\"b\":{\"x\":5},\"c\":3}\n")]
    [InlineData("[\"a\",null,\"b\",null,\"c\"] | add", "\"abc\"\n")]
    [InlineData("[\"\",\"\",null,\"x\",\"\"] | add", "\"x\"\n")]
    [InlineData("[{a:{x:1}},{b:2},{c:3}] as $x | ($x|add|.a.x=4),$x", "{\"a\":{\"x\":4},\"b\":2,\"c\":3}\n[{\"a\":{\"x\":1}},{\"b\":2},{\"c\":3}]\n")]
    [InlineData("try add((\"a\",\"b\",false)) catch .", "\"string (\\\"ab\\\") and boolean (false) cannot be added\"\n")]
    [InlineData("try add(({}, {a:1}, false)) catch .", "\"object ({\\\"a\\\":1}) and boolean (false) cannot be added\"\n")]
    [InlineData("try add((\"a\", \"b\", error(\"later\"))) catch .", "\"later\"\n")]
    public async Task Jq_AddPreservesObjectStringAndErrorSemantics(string filter, string expected)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Jq_AddObjectStringGrowthPreflightsAndCancels(bool strings, bool cancel)
    {
        JsonNode fragment = strings ? JsonValue.Create("xxxxxxxxxxxxxxxx") : new JsonObject { ["a"] = 1 };
        var root = new BenchmarkMemoryBudget(1024);
        using var cancellation = new CancellationTokenSource();
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            if (cancel) Assert.Throws<OperationCanceledException>(() => runtime.AddValues(Generate()));
            else Assert.True(Assert.Throws<JqQuotaException>(() => runtime.AddValues(Generate())).IsMemoryAdmission);
            Assert.Equal(strings ? "\"xxxxxxxxxxxxxxxx\"" : "{\"a\":1}", fragment.ToJsonString());
        }
        Assert.Equal(0, root.ReservedBytes);

        IEnumerable<JsonNode?> Generate()
        {
            for (int index = 0; index < 100; index++)
            {
                if (cancel && index == 3) cancellation.Cancel();
                yield return fragment;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_AddConsumesLongInputStreamsWithLinearStorage(bool strings)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(strings
            ? string.Concat(Enumerable.Repeat("\"x\"\n", 10000))
            : string.Concat(Enumerable.Range(0, 1024).Select(index => "{\"k" + index + "\":" + index + "}\n")));
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "add(inputs) | length")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(strings ? "10000\n" : "1024\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_AddPrivateObjectChecksCopiedValueDepth()
    {
        JsonNode deep = JsonValue.Create(0);
        for (int depth = 0; depth < 64; depth++) deep = new JsonArray(deep);
        var source = new JsonObject { ["a"] = deep };
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None))
        {
            var error = Assert.Throws<JqQuotaException>(() => new JqRuntime(budget).AddValues(
                new JsonNode?[] { new JsonObject(), new JsonObject(), source }));
            Assert.False(error.IsMemoryAdmission);
            Assert.Equal("value nesting limit exceeded", error.Message);
            Assert.Same(deep, source["a"]);
        }
        Assert.Equal(0, root.ReservedBytes);
    }
}
