using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"a\ud83d\ude80b\"|[match(\"(?<char>.)\";\"g\")|[.offset,.length,.string,.captures[0].string]]", "[[0,1,\"a\",\"a\"],[1,1,\"\\ud83d\\ude80\",\"\\ud83d\\ude80\"],[2,1,\"b\",\"b\"]]\n")]
    [InlineData("\"\ud83d\ude80\ud83d\ude80x\"|match(\"(?<pair>.)\\\\k<pair>\")|[.offset,.length,.string]", "[0,2,\"\\ud83d\\ude80\\ud83d\\ude80\"]\n")]
    [InlineData("\"a\ud83d\ude80b\"|[scan(\".{2}\")]", "[\"a\\ud83d\\ude80\"]\n")]
    [InlineData("\"\ud83d\ude80\ud83d\ude80b\"|gsub(\"(.)\\\\1\";\"X\")", "\"Xb\"\n")]
    [InlineData("\"\u00e9\ud83d\ude80x\"|match(\"(?<=\ud83d\ude80)x\")|[.offset,.length]", "[2,1]\n")]
    [InlineData("\"\u00e9\ud83d\ude80\"|[splits(\".\")]", "[\"\",\"\",\"\"]\n")]
    [InlineData("\"\ud83d\ude80\"|test(\"^[^x]$\")", "true\n")]
    [InlineData("\"\ud83d\ude80\"|test(\"^..$\")", "false\n")]
    public async Task Jq_RegexScalarMatchingPreservesNativeResults(string filter, string expected)
    {
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nac", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }
}
