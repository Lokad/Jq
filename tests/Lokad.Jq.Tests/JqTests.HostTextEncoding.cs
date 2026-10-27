using Lokad.Jq.Benchmarking;
using Lokad.Jq.Helpers;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("high")]
    [InlineData("low")]
    [InlineData("pair")]
    public async Task Jq_HostTextEncodingAgreesAcrossRawJsonAndRegex(string kind)
    {
        var (text, expected) = HostTextFixture(kind);
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nr", "--arg", "value", text, "$value, ($value|tojson|fromjson), ($value|test(\".\")), ($value|gsub(\".\";\"x\"))"], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected + "\n" + expected + "\ntrue\n" + (kind == "low" ? "xxx" : "x") + "\n",
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("high", "halt")]
    [InlineData("high", "missing-input")]
    [InlineData("high", "missing-program")]
    [InlineData("high", "compile-program")]
    [InlineData("low", "halt")]
    [InlineData("low", "missing-input")]
    [InlineData("low", "missing-program")]
    [InlineData("low", "compile-program")]
    [InlineData("pair", "halt")]
    [InlineData("pair", "missing-input")]
    [InlineData("pair", "missing-program")]
    [InlineData("pair", "compile-program")]
    public async Task Jq_HostTextEncodingPreservesDiagnosticsAndStatus(string kind, string scenario)
    {
        var (text, expected) = HostTextFixture(kind);
        string path = "/" + text;
        var host = new MockFileSystem();
        if (scenario == "compile-program") host.AddFile(path, "(");
        string[] arguments = scenario switch
        {
            "halt" => ["-n", "--arg", "value", text, "$value|halt_error(7)"],
            "missing-program" or "compile-program" => ["-n", "-f", path],
            _ => [".", path]
        };
        int status = scenario == "halt" ? 7 : scenario == "compile-program" ? 3 : 2;
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, []);
        Assert.Equal(status, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(scenario == "halt" ? expected : scenario == "compile-program"
            ? "jq: unexpected token <end> at line 1 column 2 (file \"/" + expected + "\")\n"
            : "jq: cannot open /" + expected + "\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(scenario == "compile-program" ? 1 : 0, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_HostTextEncodingCountMatchesReplacementAcrossCooperativeChunks()
    {
        string text = new string('a', 65535) + "\uD800\uD83D\uDE80\uDC00";
        Assert.Equal(65545, Utf8Text.GetByteCount(text, CancellationToken.None));
        var bytes = new byte[65545];
        Assert.Equal(bytes.Length, Utf8Text.Encode(text, bytes));
        Assert.Equal(new byte[] { 0xEF, 0xBF, 0xBD, 0xF0, 0x9F, 0x9A, 0x80, 0xEF, 0xBF, 0xBD }, bytes[65535..]);
        Assert.Throws<System.Text.DecoderFallbackException>(() => Utf8Text.Decode(new byte[] { 0xFF }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_HostTextEncodingStopsAtWriteBoundariesAndClosesInputs(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem { AppendRemainsOpen = cancelled };
        host.AddFile("/input", "null\nnull\n");
        if (cancelled) host.BeforeByteAppend = () => cancellation.Cancel();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "--arg", "value", "\uD800", "$value", "/input")));
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        else Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, cancellation.Token));
        Assert.Equal(cancelled ? "" : "\uFFFD\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private static (string Text, string Expected) HostTextFixture(string kind) => kind switch
    {
        "high" => ("\uD800", "\uFFFD"),
        "low" => ("a\uDC00b", "a\uFFFDb"),
        _ => ("\uD83D\uDE80", "\uD83D\uDE80")
    };
}
