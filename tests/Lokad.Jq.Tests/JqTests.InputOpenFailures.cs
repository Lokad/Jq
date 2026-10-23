using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("outer", false)]
    [InlineData("outer", true)]
    [InlineData("exit", false)]
    [InlineData("exit", true)]
    [InlineData("explicit", false)]
    [InlineData("explicit", true)]
    [InlineData("catch", false)]
    [InlineData("catch", true)]
    [InlineData("raw", false)]
    [InlineData("raw", true)]
    [InlineData("slurp", false)]
    [InlineData("slurp", true)]
    [InlineData("first", false)]
    [InlineData("first", true)]
    [InlineData("halt", false)]
    [InlineData("halt", true)]
    [InlineData("halt error", false)]
    [InlineData("halt error", true)]
    public async Task Jq_InputOpenFailuresRecoverOutsideLanguageErrors(string mode, bool missingFirst)
    {
        var host = new MockFileSystem();
        host.AddFile("/first", "1\n");
        host.AddFile("/later", "2\n3\n");
        string[] options = mode switch
        {
            "exit" => ["-ce", "."],
            "explicit" => ["-nc", "inputs"],
            "catch" => ["-nc", "try inputs catch \"caught\""],
            "raw" => ["-Rc", "."],
            "slurp" => ["-sc", "."],
            "first" => ["-nc", "first(inputs)"],
            "halt" => ["-c", "halt"],
            "halt error" => ["-c", "\"stop\"|halt_error(7)"],
            _ => ["-c", "."],
        };
        bool early = !missingFirst && mode is "first" or "halt" or "halt error";
        string before = missingFirst ? "" : mode == "raw" ? "\"1\"\n" : "1\n";
        string expected = mode switch
        {
            "explicit" or "catch" => before + "2\n3\n",
            "slurp" => missingFirst ? "[2,3]\n" : "[1,2,3]\n",
            "first" => missingFirst ? "2\n" : "1\n",
            "halt" or "halt error" => "",
            "raw" => before + "\"2\"\n",
            _ => before + "2\n",
        };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var invocation = BuildInvocation("jq", [.. options, ..(missingFirst ? new[] { "/missing", "/later" } : new[] { "/first", "/missing", "/later" })]);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(early ? mode == "halt error" ? 7 : 0 : 2, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal((early ? "" : "jq: cannot open /missing\n") + (mode == "halt error" ? "stop" : ""),
            host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(early ? 1 : missingFirst ? 1 : 2, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InputOpenFailuresCannotReplaceLaterAdmissionRefusal()
    {
        var host = new MockFileSystem();
        host.AddFile("/later", "\"" + new string('x', 256 * 1024) + "\"");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "try inputs catch \"caught\"", "/missing", "/later")));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: cannot open /missing\njq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InputOpenFailuresStopWhenTheirDiagnosticCloses()
    {
        var host = new MockFileSystem { AppendRemainsOpen = false };
        host.AddFile("/later", "42");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "inputs", "/missing", "/later")));
        Assert.Equal(2, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: cannot open /missing\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InputOpenFailuresKeepActualDiagnosticWriteFailure()
    {
        var inner = new MockFileSystem();
        inner.AddFile("/later", "42");
        var host = new DiagnosticHost(inner) { StderrResult = JqAppendResult.Failure(7) };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "try inputs catch \"caught\"", "/missing", "/later")));
        Assert.Equal(7, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: cannot open /missing\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, inner.ReadBytesCallCount);
    }

    [Fact]
    public async Task Jq_InputOpenFailuresRemainPrimaryDuringLaterClose()
    {
        var host = new MockFileSystem { CloseFailure = new JqOperationError("storage unavailable") };
        host.AddFile("/later", "42");
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", ".", "/missing", "/later")));
        Assert.Equal(2, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("42\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: cannot open /missing\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InputOpenRecoveryPreservesCatchableParseErrors()
    {
        var host = new MockFileSystem();
        host.AddFile("/bad", "!");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "try inputs catch \"caught\"", "/bad")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"caught\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
    }
}
