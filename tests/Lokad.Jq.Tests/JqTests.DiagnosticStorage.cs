using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_EmptyDiagnosticPreflightsQueueStorage()
    {
        var root = new BenchmarkMemoryBudget(0);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(Assert.Throws<JqQuotaException>(() => context.EmitStderr("")).IsMemoryAdmission);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16 * 1024);
            Assert.Empty(context.PendingStderr);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    public void Jq_DiagnosticBurstRefusalPreservesQueuedMessagesAndReleasesBacking(int length)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            string message = new('a', length);
            int count = 0;
            Assert.True(Assert.Throws<JqQuotaException>(() =>
            {
                for (; count < 100000; count++) context.EmitStderr(message);
            }).IsMemoryAdmission);
            Assert.InRange(count, 1, 4096);
            Assert.Equal(count, context.PendingStderr.Count);
            Assert.All(context.PendingStderr, chunk => Assert.Equal(length, chunk.Length));
            Assert.True(root.ReservedBytes >= 16L * count);
            context.ReleaseDrainedStderr();
            Assert.Empty(context.PendingStderr);
            Assert.InRange(root.ReservedBytes, 0, 352);
            Assert.InRange(((List<ReadOnlyMemory<byte>>)context.PendingStderr).Capacity, 0, 4);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("try (range(100000) | \"\" | stderr | empty) catch \"caught\"")]
    [InlineData("(range(100000) | \"\" | stderr | empty)?")]
    public async Task Jq_EmptyDiagnosticBurstRefusalIsTerminalAndClosesOwnedInput(string source)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "null");
        host.AddFile("/later", "invalid JSON");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (" + source + ")", "/input", "/later")));
        Assert.Equal(5, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public void Jq_DiagnosticAdmissionCancellationDoesNotPublishAMessage()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var memory = new AdmissionCallbackBudget(root, cancellation.Cancel);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token)))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => context.EmitStderr(new string('a', 10000)));
            Assert.Empty(context.PendingStderr);
        }
        Assert.Equal(0, root.ReservedBytes);
    }
}
