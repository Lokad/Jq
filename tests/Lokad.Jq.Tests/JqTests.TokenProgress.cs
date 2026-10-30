using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("ascii")]
    [InlineData("unicode")]
    [InlineData("escapes")]
    [InlineData("key")]
    [InlineData("fraction")]
    [InlineData("exponent")]
    public async Task Jq_LongTokensPreserveValuesAcrossSmallReads(string shape)
    {
        const int length = 65536;
        string text = shape switch
        {
            "unicode" => new string('é', length) + "🚀",
            "escapes" => string.Concat(Enumerable.Repeat("\n\\\"", length / 4)),
            _ => new string('x', length),
        };
        string input = shape switch
        {
            "unicode" => "\"" + text + "\"",
            "key" => "{" + JsonSerializer.Serialize(text) + new string(' ', 4096) + ":12}",
            "fraction" => "0.125" + new string('0', length),
            "exponent" => "1." + new string('0', length) + "e-3",
            _ => JsonSerializer.Serialize(text),
        };
        string filter = shape == "key" ? "keys[0] | length"
            : shape is "fraction" or "exponent" ? "." : "length";
        string expected = shape switch
        {
            "fraction" => "0.125\n",
            "exponent" => "0.001\n",
            "unicode" => "65537\n",
            "escapes" => "49152\n",
            _ => "65536\n",
        };
        foreach (int chunk in new[] { 1, 257, 8192 })
        {
            var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
            var host = new MockFileSystem();
            // The token crosses both host reads and independent file sources.
            host.AddFile("/first", input[..(input.Length / 2)]);
            host.AddFile("/last", input[(input.Length / 2)..]);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter, "/first", "/last")));
            Assert.Equal(0, await command.ExecuteAsync(new ChunkedHost(host, chunk), memory, CancellationToken.None));
            Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(2, host.ClosedDescriptors.Count);
            Assert.Equal(0, host.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("\"abc\\q")]
    [InlineData("\"abc\\u12X")]
    [InlineData("\"abc\n")]
    [InlineData("00")]
    [InlineData("-00")]
    public async Task Jq_PendingTokenErrorsStopAtTheDefinitiveByte(string prefix)
    {
        var host = new MockFileSystem();
        host.AddFile("/input", prefix + new string(' ', 65536));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".", "/input")));
        Assert.Equal(5, await command.ExecuteAsync(new ChunkedHost(host, 1), memory, CancellationToken.None));
        Assert.Equal(prefix.Length, host.ReadBytesCallCount);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Contains("parse error", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_LongPendingTokensHonorCancellationAndAdmission(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.AddFile("/input", "\"" + new string('x', 262144) + "\"");
        host.BeforeByteRead = _ =>
        {
            if (cancel && host.ReadBytesCallCount == 8) cancellation.Cancel();
        };
        var memory = new BenchmarkMemoryBudget(cancel ? 1024 * 1024 : 32 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "length", "/input")));
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                command.ExecuteAsync(new ChunkedHost(host, 257), memory, cancellation.Token));
        else
            Assert.Equal(5, await command.ExecuteAsync(new ChunkedHost(host, 257), memory, cancellation.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
