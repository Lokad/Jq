using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{}", "{}", true)]
    [InlineData("[]", "[]", true)]
    [InlineData("[]", "[{}]", false)]
    [InlineData("[{}, {\"x\":{}}]", "[{\"x\":{}}]", true)]
    [InlineData("[{\"x\":{}}]", "[{\"x\":{}},{}]", true)]
    [InlineData("[{}]", "[{\"x\":null}]", false)]
    [InlineData("[null, false, {}, []]", "[null,{},[]]", true)]
    [InlineData("[{}, []]", "[null]", false)]
    [InlineData("{\"x\":[1,2]}", "{\"x\":[2,2]}", true)]
    [InlineData("{\"x\":null}", "{\"y\":null}", false)]
    public async Task Jq_ContainmentPreservesEmptyAndNestedSemantics(string input, string needle, bool expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "contains(" + needle + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected ? "true\n" : "false\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("{}", "{}")]
    [InlineData("[]", "[]")]
    [InlineData("[{}]", "[{}]")]
    [InlineData("[{\"x\":{}}]", "[{\"x\":{}}]")]
    [InlineData("[{}]", "[{\"x\":null}]")]
    public void Jq_ContainmentObservesCancellationWithoutScalarComparisons(string input, string needle)
    {
        JsonNode? container = JsonNode.Parse(input);
        JsonNode? contained = JsonNode.Parse(needle);
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, cancellation.Token);
        var runtime = new JqRuntime(budget);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => runtime.Contains(container, contained));
    }

    [Fact]
    public void Jq_ContainmentDoesNotAllocatePerCandidate()
    {
        var haystack = new JsonArray();
        var needles = new JsonArray();
        for (int index = 0; index < 1000; index++)
        {
            haystack.Add(new JsonObject());
            needles.Add(new JsonObject { ["x"] = new JsonObject() });
        }
        haystack.Add(new JsonObject { ["x"] = new JsonObject() });
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        Assert.True(runtime.Contains(haystack, needles));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool result = runtime.Contains(haystack, needles);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(result);
        Assert.InRange(allocated, 0, 512 * 1024);
        Assert.Equal(1001, haystack.Count);
        Assert.Equal(1000, needles.Count);
    }
}
