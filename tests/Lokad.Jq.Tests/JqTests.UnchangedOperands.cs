using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false, ".+[]")]
    [InlineData(true, ".+{}")]
    [InlineData(true, ".*{}")]
    public async Task Jq_EmptyRightContainersBorrowAcrossRepeatedOperations(bool objectInput, string operation)
    {
        string array = "[" + string.Join(',', Enumerable.Range(0, 128)) + "]";
        var host = new MockFileSystem();
        host.SetStandardInput(objectInput ? "{\"items\":" + array + "}\n" : array + "\n");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-c", ". as $x | range(128) | $x | " + operation + " | empty"], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("ltrimstr")]
    [InlineData("rtrimstr")]
    [InlineData("trimstr")]
    public async Task Jq_EmptyAffixesDoNotAdmitBorrowedStringPayload(string operation)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(JsonSerializer.Serialize(new string('a', 8192)) + "\n");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-c", ". as $x | range(128) | $x | " + operation + "(\"\") | empty"], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_NestedEmptyMergeStillBuildsAnIndependentParent()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":{\"x\":[1]}}\n");
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-c", ". as $original | . * {a:{}} | .a.x[0]=9 | [.,$original]"], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("[{\"a\":{\"x\":[9]}},{\"a\":{\"x\":[1]}}]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public void Jq_UnchangedOperationsKeepAsymmetricOwnershipAndCancellation()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, root, cancellation.Token);
        var runtime = new JqRuntime(budget);
        var array = new JqArray(1);
        var obj = new JqObject(NativeFixtures.Budget()) { ["a"] = 1 };
        Assert.Same(array, runtime.Add(array, new JqArray()));
        Assert.Same(obj, runtime.Add(obj, new JqObject(NativeFixtures.Budget())));
        Assert.Same(obj, runtime.Multiply(obj, new JqObject(NativeFixtures.Budget())));
        Assert.Equal(0, root.ReservedBytes);
        Assert.NotSame(array, runtime.Add(new JqArray(), array));
        Assert.NotSame(obj, runtime.Add(new JqObject(NativeFixtures.Budget()), obj));
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => runtime.Multiply(obj, new JqObject(NativeFixtures.Budget())));
        Assert.ThrowsAny<OperationCanceledException>(() => runtime.TrimAffix(NativeFixtures.Create("a"), NativeFixtures.Create(""), true, false));
    }
}
