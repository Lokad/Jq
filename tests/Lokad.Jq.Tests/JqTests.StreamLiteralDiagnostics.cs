using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("!\n", "Invalid numeric literal at line 2, column 0")]
    [InlineData("1x\n", "Invalid numeric literal at line 2, column 0")]
    [InlineData("truex\n", "Invalid literal at line 2, column 0")]
    [InlineData("nul\n", "Invalid literal at line 2, column 0")]
    [InlineData("1x", "Invalid numeric literal at EOF at line 1, column 2")]
    public async Task Jq_StreamLiteralDiagnosticsClassifyBeforeReset(string input, string message)
    {
        foreach (bool errors in new[] { false, true })
        foreach (int chunk in new[] { 1, 3, 8192 })
        {
            var inner = new MockFileSystem();
            inner.AddFile("/records", input);
            var memory = new BenchmarkMemoryBudget(64 * 1024);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", errors ? "--stream-errors" : "--stream", ".", "/records")));
            Assert.Equal(errors ? 0 : 5, await command.ExecuteAsync(new ChunkedHost(inner, chunk), memory, CancellationToken.None));
            string errorEvent = errors ? "[" + JsonSerializer.Serialize(message) + ",[]]\n" : "";
            Assert.Equal(errorEvent, inner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(errors ? "" : "jq: parse error: " + message + "\n", inner.GetOutput(JqFileDescriptor.StdErr));
            Assert.Single(inner.ClosedDescriptors);
            Assert.Equal(0, inner.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
    }
}
