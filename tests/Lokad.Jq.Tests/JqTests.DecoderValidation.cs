using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("base64d", "first")]
    [InlineData("base64d", "middle")]
    [InlineData("base64d", "last")]
    [InlineData("base64d", "dangling")]
    [InlineData("urid", "first")]
    [InlineData("urid", "middle")]
    [InlineData("urid", "last")]
    [InlineData("urid", "dangling")]
    public void Jq_DecoderValidationRejectsLongMalformedTextBeforeOutputAdmission(string format, string position)
    {
        JqValue input = NativeFixtures.Create(MalformedDecoderText(format, position));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var error = Assert.Throws<JqException>(() => runtime.FormatValue(format, input).GetValue<string>());
            Assert.EndsWith(format == "urid" ? "is not a valid uri encoding"
                : position == "dangling" ? "trailing base64 byte found" : "is not valid base64 data", error.Message, StringComparison.Ordinal);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
            Assert.InRange(memory.PeakBytes, 1, 16 * 1024);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("base64d", "first")]
    [InlineData("base64d", "middle")]
    [InlineData("base64d", "last")]
    [InlineData("base64d", "dangling")]
    [InlineData("urid", "first")]
    [InlineData("urid", "middle")]
    [InlineData("urid", "last")]
    [InlineData("urid", "dangling")]
    public async Task Jq_DecoderValidationKeepsLongSyntaxErrorsCatchableAndPreservesPrefix(string format, string position)
    {
        // Retain the one native copy of the host-supplied argument.
        var memory = new BenchmarkMemoryBudget(1024 * 1024 + 64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--arg", "text", MalformedDecoderText(format, position),
            "\"ready\", (try ($text|@" + format + "|length) catch \"caught\")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("\"ready\"\n\"caught\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("base64d")]
    [InlineData("urid")]
    public async Task Jq_DecoderValidationStillRefusesNecessaryOutputOutsideHandlers(string format)
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024 + 64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--arg", "text", new string('A', 1048576),
            "\"ready\", (try ($text|@" + format + "|length) catch \"caught\")")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_UriDecoderAdmitsDecodedCapacityAndRefundsScratch()
    {
        const int count = 32768;
        JqValue input = NativeFixtures.Create(string.Concat(Enumerable.Repeat("%61", count)));
        string expected = new('a', count);
        var memory = new BenchmarkMemoryBudget(3 * count);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None))
        {
            Assert.Equal(expected, new JqRuntime(budget).FormatValue("urid", input).GetValue<string>());
            Assert.Equal(80 + 2 * count, memory.PeakBytes);
            Assert.Equal(80 + count, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_UriDecoderCountMatchesUnicodeAndHostSurrogateReplacement()
    {
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
        var runtime = new JqRuntime(budget);
        Assert.Equal("é🚀+\0", runtime.FormatValue("urid", NativeFixtures.Create("é%F0%9F%9A%80+%00")).GetValue<string>());
        Assert.Equal("a\uFFFDbA", runtime.FormatValue("urid", NativeFixtures.Create("a\uD800b%41")).GetValue<string>());
        Assert.Equal("\uFFFD", runtime.FormatValue("urid", NativeFixtures.Create("\uDC00")).GetValue<string>());
        Assert.Throws<JqException>(() => runtime.FormatValue("urid", NativeFixtures.Create("%FF")).GetValue<string>());
    }

    [Theory]
    [InlineData("base64d", "stream")]
    [InlineData("urid", "stream")]
    [InlineData("base64d", "closed")]
    [InlineData("urid", "closed")]
    [InlineData("base64d", "cancelled")]
    [InlineData("urid", "cancelled")]
    public async Task Jq_DecoderValidationKeepsStreamsBoundedAndStopsAtHostBoundaries(string format, string mode)
    {
        const int records = 2500;
        string input = format == "base64d" ? "YQ==\n" : "%61\n";
        using var cancellation = new CancellationTokenSource();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = mode != "closed" };
        host.AddFile("/input", string.Concat(Enumerable.Repeat(input, records)));
        host.AddFile("/later", input);
        int writes = 0;
        if (mode == "cancelled") host.BeforeByteAppend = () => { if (++writes == 3) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rr", "@" + format, "/input", "/later")));
        if (mode == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        else Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.Equal(string.Concat(Enumerable.Repeat("a\n", mode == "stream" ? records + 1 : mode == "closed" ? 1 : 2)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(mode == "stream" ? 2 : 1, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private static string MalformedDecoderText(string format, string position)
    {
        const int length = 1048576;
        if (position == "dangling") return format == "base64d" ? new string('A', length + 1) : new string('a', length - 1) + "%";
        string invalid = format == "base64d" ? "!" : "%ZZ";
        int start = position == "first" ? 0 : position == "middle" ? length / 2 : length - invalid.Length;
        return new string('A', start) + invalid + new string('A', length - start - invalid.Length);
    }
}
