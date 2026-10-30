using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_ByteCapturesProjectLookaroundAfterAMixedUnicodePrefix()
    {
        string prefix = string.Concat(Enumerable.Repeat("aé🚀", 1000));
        var host = new MockFileSystem();
        host.SetStandardInput(System.Text.Json.JsonSerializer.Serialize(prefix + "終x"));
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "match(\"(?<=(?<before>🚀))(?<current>終)(?<missing>z)?(?=(?<ahead>x))\")")));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("{\"offset\":3000,\"length\":1,\"string\":\"終\",\"captures\":["
            + "{\"offset\":2999,\"length\":1,\"string\":\"\\ud83d\\ude80\",\"name\":\"before\"},"
            + "{\"offset\":3000,\"length\":1,\"string\":\"終\",\"name\":\"current\"},"
            + "{\"offset\":-1,\"length\":0,\"string\":null,\"name\":\"missing\"},"
            + "{\"offset\":3001,\"length\":1,\"string\":\"x\",\"name\":\"ahead\"}]}\n",
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Theory]
    [InlineData("[match(\"\";\"g\")|.offset]", "[0,1,2,3]\n")]
    [InlineData("[scan(\"(.)\")]", "[[\"a\"],[\"é\"],[\"\\ud83d\\ude80\"]]\n")]
    [InlineData("[splits(\"\")]", "[\"\",\"a\",\"é\",\"\\ud83d\\ude80\",\"\"]\n")]
    [InlineData("gsub(\"\";\"-\")", "\"-a-é-\\ud83d\\ude80-\"\n")]
    public async Task Jq_ByteCaptureIterationKeepsUnicodeBoundaries(string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\"aé🚀\"");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_RegexCoordinateAdmissionPreservesTerminalRefusalAndCancellation(bool cancel)
    {
        const int allowance = 128 * 1024;
        var root = new BenchmarkMemoryBudget(allowance);
        var counted = new CountingMemoryBudget(root);
        using var cancellation = new CancellationTokenSource();
        bool armed = false;
        var memory = new AdmissionCallbackBudget(counted, () => { if (armed) cancellation.Cancel(); });
        IMemoryReservation? filler = null;
        var engine = new ObservedRegexEngine((operation, _, _) =>
        {
            if (operation != "detailed") return;
            if (cancel) armed = true;
            else Assert.True(root.TryReserve(allowance - root.ReservedBytes, out filler));
        });
        var host = new MockFileSystem();
        host.AddFile("/input", "\"" + new string('é', 1000) + "x\"");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r",
            "\"prefix\", (try match(\"x\") catch \"caught\"), \"later\"", "/input")));
        try
        {
            if (cancel)
            {
                var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    command.ExecuteAsync(host, engine, memory, cancellation.Token));
                Assert.Equal(cancellation.Token, failure.CancellationToken);
                Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            }
            else
            {
                Assert.Equal(5, await command.ExecuteAsync(host, engine, memory, cancellation.Token));
                Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
                Assert.Equal(1, counted.Counts.Refusals);
            }
            Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Single(host.ClosedDescriptors);
            Assert.Equal(engine.Created, engine.Disposed);
        }
        finally { filler?.Dispose(); }
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, root.ActiveScopes);
    }

    [Fact]
    public async Task Jq_ShortUnicodeSubstitutionsKeepAdmissionsBatched()
    {
        long firstReservations = 0;
        foreach (int count in new[] { 1, 1000 })
        {
            var root = new BenchmarkMemoryBudget(64 * 1024);
            var memory = new CountingMemoryBudget(root);
            var host = new MockFileSystem();
            host.SetStandardInput(string.Concat(Enumerable.Repeat("\"é🚀x\"\n", count)));
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "gsub(\"x\";\"y\") | empty")));
            Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(0, root.ReservedBytes);
            Assert.Equal(0, memory.Counts.SameSizeResizes);
            if (count == 1) firstReservations = memory.Counts.Reservations;
            else Assert.InRange(memory.Counts.Reservations, 1, firstReservations + 4);
        }
    }
}
