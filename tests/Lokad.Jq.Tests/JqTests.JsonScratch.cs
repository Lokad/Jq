using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("\"ok\"")]
    [InlineData("{\"x\":[1,\"ok\"]}")]
    public void Jq_JsonScratchRefundsConversionWithoutRefundingDecodedValues(string json)
    {
        string text = json + new string(' ', 65536 - json.Length);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var counted = new CountingMemoryBudget(memory);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, counted, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            var values = new List<JqValue?>();
            for (int index = 0; index < 32; index++)
            {
                values.Add(runtime.ParseJson(text));
                Assert.InRange(memory.ReservedBytes, 0, 32 * 1024);
            }
            Assert.All(values, value => Assert.Equal(json, value?.ToJsonString() ?? "null"));
            if (json == "null")
            {
                Assert.Equal(0, memory.ReservedBytes);
                Assert.Equal(65536, memory.PeakBytes);
                Assert.Equal(32, counted.Counts.Reservations);
                Assert.Equal(0, counted.Counts.Resizes);
                Assert.Equal(32, counted.Counts.ReservationDisposals);
            }
            else Assert.True(memory.ReservedBytes > 0);
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, counted.Counts.Refusals);
        Assert.Equal(0, counted.Counts.BytesReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_JsonScratchRefundsMalformedAndTrailingInput(bool trailing)
    {
        string text = (trailing ? "null" : "") + new string(' ', 65536) + (trailing ? "false" : "?");
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            for (int index = 0; index < 32; index++)
            {
                var failure = Assert.Throws<JqException>(() => runtime.ParseJson(text));
                if (trailing) Assert.Equal("expected a single JSON value", failure.Message);
                else Assert.IsAssignableFrom<System.Text.Json.JsonException>(failure.InnerException);
                Assert.Equal(0, memory.ReservedBytes);
            }
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("\uFEFFnull", "null")]
    [InlineData("lone-surrogate", "\"\uFFFD\"")]
    [InlineData("\"🚀\"", "\"\\ud83d\\ude80\"")]
    [InlineData("-Infinity", "-1.7976931348623157E+308")]
    public void Jq_JsonScratchPreservesBomUnicodeAndNonFiniteParsing(string text, string expected)
    {
        if (text == "lone-surrogate") text = new string(['"', (char)0xD800, '"']);
        var memory = new BenchmarkMemoryBudget(16 * 1024);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None))
        {
            var runtime = new JqRuntime(budget);
            Assert.Equal(expected, runtime.Serialize(runtime.ParseJson(text), false, null, false));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_JsonScratchCancelsAfterAdmissionBeforeEncoding()
    {
        string text = "null" + new string(' ', 65532);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var cancellation = new CancellationTokenSource();
        var hooked = new AdmissionCallbackBudget(memory, cancellation.Cancel);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, hooked, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var failure = Assert.Throws<OperationCanceledException>(() => runtime.ParseJson(text));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.InRange(allocated, 0, 32 * 1024);
            Assert.Equal(0, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_JsonScratchStreamsRepeatedParsingWithinOneInput(bool close)
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = !close };
        host.SetStandardInput("null" + new string(' ', 65532));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rrs",
            "\"ready\", (. as $t | range(0;32) | $t | fromjson)")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("ready\n" + (close ? "" : string.Concat(Enumerable.Repeat("null\n", 32))), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(close ? 1 : 33, host.AppendCallCount);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Fact]
    public async Task Jq_JsonScratchCancellationRetainsEarlierOutputAndRefunds()
    {
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.SetStandardInput("null" + new string(' ', 65532));
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 3) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-Rrs",
            "\"ready\", (. as $t | range(0;32) | $t | fromjson)")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.Equal("ready\nnull\nnull\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData("try ($text|fromjson) catch \"caught\"")]
    [InlineData("($text|fromjson)?")]
    public async Task Jq_FromJsonBorrowsNativeInputWithoutConversionScratch(string operation)
    {
        var memory = new BenchmarkMemoryBudget(96 * 1024);
        var host = new MockFileSystem();
        string text = "null" + new string(' ', 65532);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr", "--arg", "text", text,
            "\"ready\", (" + operation + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("ready\nnull\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
