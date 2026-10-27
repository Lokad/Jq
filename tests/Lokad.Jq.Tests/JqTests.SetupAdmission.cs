using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("information-tail", false)]
    [InlineData("information-tail", true)]
    [InlineData("flags", false)]
    [InlineData("flags", true)]
    [InlineData("duplicate-binding", false)]
    [InlineData("duplicate-binding", true)]
    [InlineData("information-environment", false)]
    [InlineData("information-environment", true)]
    [InlineData("duplicate-environment", false)]
    [InlineData("duplicate-environment", true)]
    public async Task Jq_SetupAdmitsRetainedStateWithoutChargingIgnoredArguments(string kind, bool governed)
    {
        long? admissionCalls = null;
        foreach (int count in new[] { 1, 2048 })
        {
            string[] arguments = kind switch
            {
                "information-tail" => ["--build-configuration", .. Enumerable.Repeat("ignored", count)],
                "flags" => [.. Enumerable.Repeat("-n", count), "1"],
                "duplicate-binding" => ["-nr", .. Enumerable.Range(0, count).SelectMany(_ => new[] { "--arg", "x", "value" }), "$x"],
                "duplicate-environment" => ["-nr", "$ENV.SAME"],
                _ => ["--build-configuration"]
            };
            JqEnvironmentVariable[] environment = kind == "information-environment"
                ? Enumerable.Range(0, count).Select(i => new JqEnvironmentVariable($"V{i}", "value")).ToArray()
                : kind == "duplicate-environment"
                    ? Enumerable.Range(0, count).Select(i => new JqEnvironmentVariable("SAME", i == count - 1 ? "last" : "earlier")).ToArray()
                    : [];
            var root = new BenchmarkMemoryBudget(64 * 1024);
            var memory = new CountingMemoryBudget(root);
            JqCommandInvocation invocation;
            if (governed)
            {
                Assert.True(JqCommandInvocation.TryCreateWithStandardDescriptors("jq", arguments, environment, memory, out var admitted));
                invocation = admitted;
            }
            else invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, environment);
            using (invocation)
            {
                long snapshot = root.ReservedBytes;
                var host = new MockFileSystem();
                var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
                Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
                string expected = kind is "information-tail" or "information-environment" ? JqBuildConfiguration.Text + "\n"
                    : kind == "flags" ? "1\n" : kind == "duplicate-environment" ? "last\n" : "value\n";
                Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
                Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
                Assert.Equal(0, host.ReadBytesCallCount);
                Assert.Empty(host.ClosedDescriptors);
                Assert.Equal(snapshot, root.ReservedBytes);
                long calls = memory.Counts.Reservations + memory.Counts.Resizes;
                if (admissionCalls is { } previous) Assert.Equal(previous, calls);
                admissionCalls = calls;
            }
            Assert.Equal(0, root.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("environment")]
    [InlineData("positional")]
    [InlineData("operand")]
    [InlineData("library")]
    public async Task Jq_SetupStillAdmitsActualRetainedGrowth(string kind)
    {
        string[] arguments = kind switch
        {
            "binding" => ["-n", .. Enumerable.Range(0, 4096).SelectMany(i => new[] { "--arg", $"v{i}", "value" }), "1"],
            "positional" => ["-n", "1", "--args", .. Enumerable.Repeat("value", 4096)],
            "operand" => ["-n", "1", .. Enumerable.Repeat("/unused", 4096)],
            "library" => ["-n", .. Enumerable.Range(0, 4096).SelectMany(_ => new[] { "-L", "/unused" }), "1"],
            _ => ["-n", "1"]
        };
        JqEnvironmentVariable[] environment = kind == "environment"
            ? Enumerable.Range(0, 4096).Select(i => new JqEnvironmentVariable($"V{i}", "value")).ToArray() : [];
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, environment);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        var host = new MockFileSystem();
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        // Full retained storage also refuses diagnostic allocation; the
        // immutable emergency diagnostic must still report terminal status 5.
        Assert.Equal(2, memory.Counts.Refusals);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, root.ReservedBytes);
    }
}
