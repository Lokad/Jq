using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_SplitMissingDelimiterBorrowsPayloadButKeepsFreshWrappers(bool longer)
    {
        string text = new('x', 131072);
        JqValue input = NativeFixtures.Create(text);
        JqValue separator = NativeFixtures.Create(longer ? new string('z', text.Length + 1) : "z");
        var root = new BenchmarkMemoryBudget(256);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
        {
            var result = Assert.IsType<JqArray>(new JqRuntime(budget).Split(input, separator));
            Assert.Single(result);
            Assert.NotSame(input, result[0]);
            Assert.Equal(Assert.IsType<JqString>(input).Utf8, Assert.IsType<JqString>(result[0]).Utf8);
            Assert.Equal(152, root.PeakBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("split(\"z\")", false)]
    [InlineData(". / \"z\"", false)]
    [InlineData("split(\"a\")", true)]
    [InlineData(". / \"a\"", true)]
    public async Task Jq_SplitOnlyAdmitsActualSubstringCopies(string expression, bool copies)
    {
        string text = copies ? new string('x', 65536) + "a" + new string('x', 65536) : new string('x', 131072);
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nr", "--arg", "text", text, "\"ready\", ($text | (try (" + expression + " | length) catch \"caught\"))"], []);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(copies ? 5 : 0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(copies ? "ready\n" : "ready\n1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(copies ? "jq: memory budget exceeded\n" : "", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    public void Jq_SplitSharesWindowedSearchAcrossUnicodeBoundaries(int prefix)
    {
        string left = new('x', prefix);
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        var result = runtime.Split(NativeFixtures.Create(left + "🚀xy🚀xyend"), NativeFixtures.Create("🚀xy"));
        Assert.Equal(new[] { left, "", "end" }, Assert.IsType<JqArray>(result).Select(part => part?.GetValue<string>()));
        Assert.Equal("[\"\",\"\",\"a\"]", runtime.Split(NativeFixtures.Create("aaaaa"), NativeFixtures.Create("aa")).ToJsonString());
    }

    [Fact]
    public void Jq_SplitNearMissKeepsTheExistingPayloadAndObservesAdmissionCancellation()
    {
        string text = new('a', 262144);
        JqValue input = NativeFixtures.Create(text);
        JqValue delimiter = NativeFixtures.Create("b" + new string('a', 65536));
        var root = new BenchmarkMemoryBudget(256);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None))
            Assert.Equal(Assert.IsType<JqString>(input).Utf8,
                Assert.IsType<JqString>(new JqRuntime(budget).Split(input, delimiter)[0]).Utf8);
        Assert.Equal(0, root.ReservedBytes);
        using var cancellation = new CancellationTokenSource();
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token))
            Assert.ThrowsAny<OperationCanceledException>(() => new JqRuntime(budget).Split(input, delimiter));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("split(\",\")")]
    [InlineData(". / \",\"")]
    public async Task Jq_SplitStreamCancellationPreservesWrittenPrefixAndClosesInput(string expression)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.AddFile("/records", string.Concat(Enumerable.Repeat("a,b\n", 4096)));
        host.AddFile("/later", "unused\n");
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 32) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rc", expression, "/records", "/later")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token));
        Assert.Equal(string.Concat(Enumerable.Repeat("[\"a\",\"b\"]\n", 32)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
