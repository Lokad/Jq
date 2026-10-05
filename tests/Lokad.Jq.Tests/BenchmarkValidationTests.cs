using System.Text;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class BenchmarkValidationTests
{
    [Theory]
    [InlineData("empty", "7\n")]
    [InlineData("8", "7\n")]
    [InlineData("error(\"failed\")", "")]
    public async Task FastFailuresAndIncorrectOutputsCannotPass(string filter, string expected)
    {
        var result = await BenchmarkExecution.RunAsync(["-n", "-c", filter], [], false, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() =>
            BenchmarkExecution.Verify(result, OutputDigest.FromBytes(Encoding.UTF8.GetBytes(expected))));
    }

    [Fact]
    public async Task DiagnosticsCannotMasqueradeAsStdout()
    {
        using var host = new BenchmarkHost([], true);
        await host.AppendWhileOpenAsync(JqFileDescriptor.StdErr, "7\n"u8.ToArray(), CancellationToken.None);
        var result = host.Finish(0);
        Assert.Empty(result.Output);
        Assert.Equal("7\n"u8.ToArray(), result.Error);
        Assert.Throws<InvalidOperationException>(() => BenchmarkExecution.Verify(result, result.Digest));
    }

    [Fact]
    public async Task CapturedAndConsumedUnicodeStreamsHaveIdenticalDigests()
    {
        byte[] input = Encoding.UTF8.GetBytes("{\"text\":\"café 🚀\"}\n{\"text\":\"fin\"}\n");
        var captured = await BenchmarkExecution.RunAsync(["-r", ".text"], input, true, CancellationToken.None);
        var consumed = await BenchmarkExecution.RunAsync(["-r", ".text"], input, false, CancellationToken.None);
        Assert.Equal(Encoding.UTF8.GetBytes("café 🚀\nfin\n"), captured.Output);
        BenchmarkExecution.Verify(consumed, captured.Digest);
        Assert.Empty(consumed.Output);
        Assert.Empty(consumed.Error);
    }

    [Fact]
    public async Task CancellationIsNotConvertedToSuccessfulEmptyOutput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BenchmarkExecution.RunAsync(["."], "1"u8.ToArray(), false, cancellation.Token));
    }
}
