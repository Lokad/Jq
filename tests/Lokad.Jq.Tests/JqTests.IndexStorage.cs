using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(16384)]
    [InlineData(131072)]
    public async Task Jq_IndexBorrowsKeyPayloadAndAdmitsOnlyItsConstruction(int length)
    {
        string key = new('k', length);
        JqString nativeKey = NativeFixtures.Create(key);
        var row = new JqObject(NativeFixtures.Budget()) { ["value"] = 7 };
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None)))
        {
            var filter = new SqlIndexFilter(new LiteralFilter(row), new LiteralFilter(nativeKey));
            var result = Assert.IsType<JqObject>(await filter.Evaluate(null, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(nativeKey.Utf8, Assert.Single(result).Key.Utf8);
            var copied = Assert.IsType<JqObject>(result[key]);
            Assert.NotSame(row, copied);
            copied["value"] = 9;
            Assert.Equal(7, row["value"]?.GetValue<int>());
            Assert.InRange(root.PeakBytes, 1, 1024);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("INDEX(0;$key)|length")]
    [InlineData("[0]|INDEX($key)|length")]
    [InlineData("{($key):0}|length")]
    [InlineData("[{key:$key,value:0}]|from_entries|length")]
    public async Task Jq_IndexLongBorrowedKeysCompleteUnderSharedAdmission(string expression)
    {
        // The command owns one UTF-8 copy of its borrowed host argument.
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--arg", "key", new string('k', 131072), expression)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("\"k\"", "{\"k\":2}\n")]
    [InlineData("0", "{\"0\":2}\n")]
    [InlineData("9223372036854775807", "{\"9223372036854775807\":2}\n")]
    [InlineData("true", "{\"true\":2}\n")]
    [InlineData("null", "{\"null\":2}\n")]
    [InlineData("nan", "{\"null\":2}\n")]
    [InlineData("[1,2]", "{\"[1,2]\":2}\n")]
    [InlineData("{a:1}", "{\"{\\\"a\\\":1}\":2}\n")]
    [InlineData("(true,false)", "{\"true\":2,\"false\":2}\n")]
    public async Task Jq_IndexPreservesKeyConversionMultiplicityAndLastBinding(string key, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "INDEX((1,2);" + key + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_IndexNewSerializedKeysStillRequireAdmissionAndCloseOwnedInput()
    {
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "null\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--arg", "key", new string('k', 131072),
            "\"ready\",(try INDEX(0;[$key]) catch 42)", "/input")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_IndexCopiedRowsStillRequireAdmission()
    {
        var row = new JqArray();
        for (int index = 0; index < 6000; index++) row.Add(index);
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), new JqBudget(Utf8RegexAdapter.Instance, root, CancellationToken.None)))
        {
            var filter = new SqlIndexFilter(new LiteralFilter(row), new LiteralFilter(NativeFixtures.Create("key")));
            var failure = await Assert.ThrowsAsync<JqQuotaException>(async () => await filter.Evaluate(null, context, context.RootEnvironment).SingleAsync());
            Assert.True(failure.IsMemoryAdmission);
            Assert.Equal(6000, row.Count);
            Assert.Equal(5999, row[5999]?.GetValue<int>());
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_IndexCancellationDuringKeyEffectsKeepsTheResultPrivateAndClosesInput()
    {
        using var cancellation = new CancellationTokenSource();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "null\n");
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 2) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "INDEX(range(10);debug|tostring)", "/input")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",0]\n[\"DEBUG:\",1]\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_IndexLongKeysKeepOuterStreamsBoundedAndStopOnClosure(bool close)
    {
        const int records = 2500;
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = !close };
        host.AddFile("/input", string.Concat(Enumerable.Repeat("0\n", records)));
        host.AddFile("/later", "");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "--arg", "key", new string('k', 131072),
            "INDEX(.;$key)|length", "/input", "/later")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("1\n", close ? 1 : records)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(close ? 1 : 2, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
