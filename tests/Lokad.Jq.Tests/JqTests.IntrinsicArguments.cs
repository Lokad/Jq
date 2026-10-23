using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{COLOR:\"local\"} as $ENV | [$ENV.COLOR,env.COLOR]", "[\"local\",\"green\"]\n")]
    [InlineData("env as $saved | $saved | try path(env) catch \"fresh\"", "\"fresh\"\n")]
    public async Task Jq_IntrinsicEnvironmentKeepsLexicalAndHeapIdentity(string filter, string expected)
    {
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], [new("COLOR", "green")]);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("--arg", "ENV", "\"42\"")]
    [InlineData("--argjson", "ENV", "42")]
    [InlineData("--rawfile", "ENV", "\"42\"")]
    [InlineData("--slurpfile", "ENV", "[42]")]
    [InlineData("--arg", "ARGS", "\"42\"")]
    [InlineData("--argjson", "ARGS", "42")]
    [InlineData("--rawfile", "ARGS", "\"42\"")]
    [InlineData("--slurpfile", "ARGS", "[42]")]
    public async Task Jq_IntrinsicArgumentsPreserveNamedValues(string option, string name, string expected)
    {
        bool file = option is "--rawfile" or "--slurpfile";
        var host = new MockFileSystem();
        host.AddFile("/data", "42");
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nc", option, name, file ? "/data" : "42", "[$ENV.COLOR,env.COLOR,$ARGS.named." + name + "]"],
            [new("COLOR", "green")]);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("[\"green\",\"green\"," + expected + "]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(file ? 1 : 0, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("--rawfile", "ENV")]
    [InlineData("--slurpfile", "ENV")]
    [InlineData("--rawfile", "ARGS")]
    [InlineData("--slurpfile", "ARGS")]
    public async Task Jq_IntrinsicFileArgumentsReportMissingFiles(string option, string name)
    {
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", option, name, "/missing", "42")));
        Assert.Equal(2, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: Bad JSON in " + option + " " + name + " /missing: cannot open /missing\n",
            host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("ENV")]
    [InlineData("ARGS")]
    public async Task Jq_IntrinsicSlurpArgumentsReportMalformedFiles(string name)
    {
        var host = new MockFileSystem();
        host.AddFile("/data", "[");
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--slurpfile", name, "/data", "42")));
        Assert.Equal(2, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Contains("Bad JSON in --slurpfile " + name + " /data", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("--rawfile", "ENV")]
    [InlineData("--slurpfile", "ENV")]
    [InlineData("--rawfile", "ARGS")]
    [InlineData("--slurpfile", "ARGS")]
    public async Task Jq_IntrinsicFileArgumentsKeepTerminalCloseFailure(string option, string name)
    {
        var host = new MockFileSystem { CloseFailure = new JqOperationError("storage unavailable") };
        host.AddFile("/data", "42");
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", option, name, "/data", "42")));
        Assert.Equal(2, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error closing /data: storage unavailable\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("ENV", false)]
    [InlineData("ENV", true)]
    [InlineData("ARGS", false)]
    [InlineData("ARGS", true)]
    public async Task Jq_IntrinsicArgumentNamesKeepFirstBinding(string name, bool fileFirst)
    {
        var host = new MockFileSystem();
        host.AddFile("/data", "42");
        string[] arguments = fileFirst
            ? ["-nc", "--rawfile", name, "/data", "--arg", name, "ignored", "$ARGS.named." + name]
            : ["-nc", "--arg", name, "first", "--rawfile", name, "/missing", "$ARGS.named." + name];
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", arguments)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(fileFirst ? "\"42\"\n" : "\"first\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(fileFirst ? 1 : 0, host.ClosedDescriptors.Count);
    }

    [Theory]
    [InlineData("ENV")]
    [InlineData("ARGS")]
    public async Task Jq_IntrinsicFileArgumentsRetainAdmission(string name)
    {
        var host = new MockFileSystem();
        host.AddFile("/data", new string('x', 256 * 1024));
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--rawfile", name, "/data", "42")));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("ENV", false)]
    [InlineData("ENV", true)]
    [InlineData("ARGS", false)]
    [InlineData("ARGS", true)]
    public async Task Jq_IntrinsicFileArgumentsKeepConcurrentBorrowersIndependent(string name, bool cancel)
    {
        var first = new MockFileSystem();
        first.AddFile("/data", "42");
        var second = new MockFileSystem();
        second.AddFile("/data", "99");
        var firstHost = new MemoryBorrowHost(first);
        var secondHost = new MemoryBorrowHost(second);
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        using var invocation = BuildInvocation("jq", "-nc", "--slurpfile", name, "/data", "$ARGS.named." + name + "[0]");
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        using var cancellation = new CancellationTokenSource();
        Task<int> firstRun = command.ExecuteAsync(firstHost, memory, cancellation.Token);
        Task<int> secondRun = command.ExecuteAsync(secondHost, memory, CancellationToken.None);
        Assert.Equal("42\n"u8.ToArray(), (await firstHost.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10))).ToArray());
        Assert.Equal("99\n"u8.ToArray(), (await secondHost.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10))).ToArray());
        if (cancel) cancellation.Cancel();
        Assert.False(firstRun.IsCompleted);
        Assert.False(secondRun.IsCompleted);
        firstHost.Release.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun);
        else Assert.Equal(0, await firstRun);
        Assert.True(memory.ReservedBytes > 0);
        secondHost.Release.SetResult();
        Assert.Equal(0, await secondRun);
        Assert.Single(first.ClosedDescriptors);
        Assert.Single(second.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
