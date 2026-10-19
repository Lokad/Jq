using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("error(.)", 8192)]
    [InlineData("error(. + \"y\")", 8193)]
    public async Task Jq_ScalarReductionKeepsEscapingErrorStorageCharged(string error, int length)
    {
        var memory = new TestMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.BeforeByteAppend = () => Assert.True(memory.ReservedBytes >= 2L * length);
        string filter = "try (reduce range(2) as $i (\"\"; if $i == 0 then \"x\" * 8192 else " + error + " end)) catch length";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(length + "\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("\"x\" * 128", "128\n")]
    [InlineData("if $i % 2 == 0 then \"x\" * 128 else . end", "128\n")]
    [InlineData("\"x\" * 128 | . as $temporary | true", "4\n")]
    public async Task Jq_ScalarReductionRetainsOnlyItsLiveUpdateDemand(string update, string expected)
    {
        var root = new TestMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "reduce range(100000) as $i (\"\"; " + update + ") | tostring | length")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 100);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ScalarReductionKeepsCollectedResultsAndContainerAliasesCharged()
    {
        foreach (string filter in new[]
        {
            "[range(1000) | reduce range(2) as $_ (\"\"; \"x\" * 128)] | length",
            "reduce range(1000) as $_ ([]; . + [\"x\" * 128]) | length",
        })
        {
            var memory = new TestMemoryBudget(64 * 1024);
            var host = new MockFileSystem();
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
            Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Fact]
    public async Task Jq_ScalarReductionDoesNotRefundPersistentRegexKeys()
    {
        var memory = new TestMemoryBudget(64 * 1024);
        var engine = new FixedRegexEngine();
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "\"prefix\", (reduce range(1000) as $i (\"\"; ($i | tostring) as $p | (\"x\" | test($p)) | \"x\" * 128))")));
        Assert.Equal(5, await command.ExecuteAsync(host, new JqExecutionPolicy { RegexEngine = engine }, memory, CancellationToken.None));
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(engine.Compilations, 1, 999);
        Assert.Equal(engine.Compilations, engine.Disposals);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ScalarReplacementAdmitsItsPeakBeforeReleasingOldState()
    {
        const int limit = 128 * 1024;
        var memory = new TestMemoryBudget(limit);
        IMemoryReservation? pressure = null;
        var host = new MockFileSystem();
        host.AddFile("/first", "1\n" + new string(' ', 8190) + "2\n");
        host.AddFile("/later", "3\n");
        host.BeforeByteRead = _ =>
        {
            if (host.ReadBytesCallCount != 2) return;
            Assert.True(memory.ReservedBytes >= 32000);
            // Leave enough for the bounded input prelude, but not a replacement
            // string while the preceding accumulator is still live.
            Assert.True(memory.TryReserve(limit - memory.ReservedBytes - 4096, out pressure));
        };
        try
        {
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
                "\"prefix\", (try (reduce inputs as $_ (\"\"; \"x\" * 16000)) catch \"caught\")", "/first", "/later")));
            Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(2, host.ReadBytesCallCount);
            Assert.Single(host.ClosedDescriptors);
            Assert.Equal(0, host.OpenFileCount);
            Assert.Equal(Assert.IsAssignableFrom<IMemoryReservation>(pressure).Bytes, memory.ReservedBytes);
        }
        finally { pressure?.Dispose(); }
        Assert.Equal(0, memory.ReservedBytes);
    }
}
