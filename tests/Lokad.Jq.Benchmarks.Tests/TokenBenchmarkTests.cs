using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class TokenBenchmarkTests
{
    [Theory]
    [InlineData(256, false)]
    [InlineData(8192, false)]
    [InlineData(256, true)]
    [InlineData(8192, true)]
    public async Task FragmentedTokenDiagnosticsVerifyEverySize(int chunk, bool escaped)
    {
        foreach (int size in new[] { 131072, 262144, 524288, 1048576 })
        {
            var benchmark = new TokenBenchmarks { InputBytes = size, ReadChunk = chunk, Escaped = escaped };
            benchmark.Setup();
            Assert.True(await benchmark.Execute() > 0);
        }
    }
}
