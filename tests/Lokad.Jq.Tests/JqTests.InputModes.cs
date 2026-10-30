using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("--stream", false)]
    [InlineData("--stream-errors", false)]
    [InlineData("--stream", true)]
    public async Task Jq_RawInputKeepsByteSemanticsWithInactiveStreamOptions(string option, bool sequence)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\ufeffa\r\nb\n");
        string[] options = sequence ? [option, "--seq"] : [option];
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ["-Rc", .. options, "."])));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        string prefix = sequence ? "\u001e" : "";
        Assert.Equal(prefix + "\"\\ufeffa\\r\"\n" + prefix + "\"b\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InactiveRawStreamOptionDoesNotAllocateScannerState()
    {
        long ordinary = await Measure(false);
        long streamFlag = await Measure(true);
        Assert.Equal(ordinary, streamFlag);

        static async Task<long> Measure(bool stream)
        {
            using var budget = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
            using var context = new JqContext(new Dictionary<string, JqValue?>(), budget);
            var invocation = new JqInvocation { RawInput = true, Stream = stream };
            var host = new MockFileSystem();
            for (int i = 0; i < 100; i++)
            {
                await using var cursor = new JqInputCursor(host, invocation, context, CancellationToken.None);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                await using var cursor = new JqInputCursor(host, invocation, context, CancellationToken.None);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
