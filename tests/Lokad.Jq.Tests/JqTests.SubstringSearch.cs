using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(32765)]
    [InlineData(65530)]
    [InlineData(65534)]
    [InlineData(131071)]
    public async Task Jq_StringSearchWindowsPreserveOverlappingScalarOffsets(int prefix)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(JsonSerializer.Serialize(new string('x', prefix) + "🚀a🚀a🚀" + new string('x', 65540)));
        var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "[indices(\"🚀a🚀\"),index(\"🚀a🚀\"),rindex(\"🚀a🚀\")]")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("[[" + prefix + "," + (prefix + 2) + "]," + prefix + "," + (prefix + 2) + "]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(32768)]
    [InlineData(65537)]
    [InlineData(131073)]
    public void Jq_StringSearchLongNeedlesVerifyPrefixesAndConvertOffsets(int length)
    {
        string needle = new string('a', length / 2) + "b" + new string('a', length - length / 2 - 1);
        var text = JsonValue.Create("🚀" + needle + "x" + needle + "!");
        var key = JsonValue.Create(needle);
        using var budget = new JqBudget(CancellationToken.None);
        var runtime = new JqRuntime(budget);
        var found = Assert.IsType<JsonArray>(runtime.Indices(text, key));
        Assert.Equal(2, found.Count);
        Assert.Equal(1, found[0]?.GetValue<int>());
        Assert.Equal(length + 2, found[1]?.GetValue<int>());
        Assert.Equal(1, runtime.Index(text, key, false)?.GetValue<int>());
        Assert.Equal(length + 2, runtime.Index(text, key, true)?.GetValue<int>());
        var miss = JsonValue.Create(needle.Replace('b', 'c'));
        Assert.Null(runtime.Index(miss, key, false));
        Assert.Null(runtime.Index(miss, key, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_StringSearchMissBorrowsItsInputsWithoutAllocating(bool last)
    {
        using var budget = new JqBudget(CancellationToken.None);
        var runtime = new JqRuntime(budget);
        var input = JsonValue.Create(new string('a', 262144));
        var needle = JsonValue.Create(new string('a', 16384) + "b");
        for (int i = 0; i < 100; i++) runtime.Index(input, needle, last);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            if (runtime.Index(input, needle, last) is not null) throw new InvalidOperationException("Unexpected match.");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(131072, 65537)]
    [InlineData(262144, 65537)]
    [InlineData(262144, 131073)]
    public void Jq_StringSearchLongNeedleMissKeepsTheWholePattern(int size, int length)
    {
        string fragment = new string('a', length / 4) + "b" + new string('a', length - length / 4 - 1);
        var input = JsonValue.Create(new string('a', size));
        var needle = JsonValue.Create(fragment);
        using var budget = new JqBudget(CancellationToken.None);
        var runtime = new JqRuntime(budget);
        Assert.Null(runtime.Index(input, needle, false));
        Assert.Null(runtime.Index(input, needle, true));
        Assert.Empty(Assert.IsType<JsonArray>(runtime.Indices(input, needle)));
    }

    [Fact]
    public void Jq_StringSearchLongNeedlesPreserveOverlapsAndCancellation()
    {
        string fragment = new('a', 65537);
        using (var budget = new JqBudget(CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            var input = JsonValue.Create(fragment + "aaa");
            var needle = JsonValue.Create(fragment);
            Assert.Equal("[0,1,2,3]", runtime.Indices(input, needle).ToJsonString());
            Assert.Equal(3, runtime.Index(input, needle, true)?.GetValue<int>());
        }
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            Assert.ThrowsAny<OperationCanceledException>(() => runtime.Indices(
                JsonValue.Create(fragment + "b" + new string('a', 262144)), JsonValue.Create(fragment)));
            Assert.ThrowsAny<OperationCanceledException>(() => runtime.Index(JsonValue.Create(fragment), JsonValue.Create(fragment), true));
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_StringSearchObservesCancellationDuringResultAdmission()
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            Assert.ThrowsAny<OperationCanceledException>(() =>
                runtime.Indices(JsonValue.Create(new string('a', 65536)), JsonValue.Create("aa")));
            Assert.ThrowsAny<OperationCanceledException>(() => runtime.Index(JsonValue.Create("a"), JsonValue.Create("a"), true));
        }
        Assert.Equal(0, root.ReservedBytes);
    }
}
