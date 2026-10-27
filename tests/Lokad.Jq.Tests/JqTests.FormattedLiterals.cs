using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("html")]
    [InlineData("missing")]
    public async Task Jq_FormattedLiteralsReuseTheirConstantWithinOneInput(string format)
    {
        foreach (bool close in new[] { false, true })
        {
            var memory = new BenchmarkMemoryBudget(256 * 1024);
            var host = new MockFileSystem { AppendRemainsOpen = !close };
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
                "range(0;64) | @" + format + " \"" + new string('x', 8192) + "\" | length")));
            Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
            Assert.Equal(string.Concat(Enumerable.Repeat("8192\n", close ? 1 : 64)), host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(close ? 1 : 64, host.AppendCallCount);
            Assert.Equal(0, memory.ReservedBytes);
            Assert.Equal(0, memory.ActiveScopes);
        }
    }

    [Theory]
    [InlineData("@html \"<&>\"", "<&>\n")]
    [InlineData("@missing \"\\\\(literal)\"", "\\(literal)\n")]
    [InlineData("@html \"\\u0041\\ud83d\\ude80\"", "A🚀\n")]
    [InlineData("@missing \"line\\nnext\"", "line\nnext\n")]
    [InlineData("@missing \"\"", "\n")]
    public async Task Jq_FormattedLiteralsDecodeEscapesWithoutApplyingTheFormat(string filter, string expected)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("html")]
    [InlineData("missing")]
    public async Task Jq_FormattedLiteralsAreValidConstantModuleMetadata(string format)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "module {doc:@" + format + " \"literal\"}; 1")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("html")]
    [InlineData("missing")]
    public async Task Jq_FormattedLiteralsPreserveModuleAndImportMetadataContents(string format)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/lib/tiny.jq", "module {doc:@" + format + " \"<&>\"}; def value:42;");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/lib",
            "import \"tiny\" as m {doc:@" + format + " \"literal\"}; [(\"tiny\"|modulemeta).doc,m::value]")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("[\"<&>\",42]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FormattedLiteralsRetainTheSelectedConstantPathIdentity()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "def f: @missing \"x\"; f as $x | $x | path(f)")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("[]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FormattedLiteralsStillRequireAdmissionForCompiledSource()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "@missing \"" + new string('x', 65536) + "\"")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("try @missing ")]
    [InlineData("@missing ")]
    public async Task Jq_FormattedLiteralsStillPreflightOutputAfterEarlierWrites(string prefix)
    {
        const long limit = 512 * 1024;
        var memory = new BenchmarkMemoryBudget(limit);
        var host = new MockFileSystem();
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 4096, out pressure));
        };
        string expression = prefix + "\"" + new string('x', 32768) + "\"";
        if (prefix.StartsWith("try", StringComparison.Ordinal)) expression += " catch \"caught\"";
        else expression = "(" + expression + ")?";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "\"ready\", (" + expression + ")")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.NotNull(pressure);
        Assert.Equal(pressure.Bytes, memory.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_FormattedLiteralsObserveCancellationBetweenResults()
    {
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 3) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "\"ready\", (range(0;64)|@missing \"" + new string('x', 8192) + "\"|length)")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.Equal("ready\n8192\n8192\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
