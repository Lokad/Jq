namespace Lokad.Jq.Tests;

public sealed class HostBoundaryTests
{
    [Fact]
    public void InvocationSnapshotsArgumentsAndEnvironment()
    {
        var arguments = new[] { "-n", "." };
        var environment = new[] { new JqEnvironmentVariable("PWD", "/work") };
        var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, environment);
        arguments[1] = "empty";
        environment[0] = new JqEnvironmentVariable("PWD", "/different");

        Assert.Equal(".", invocation.Arguments[1]);
        Assert.Equal("/work", invocation.CurrentDirectory.Path);
        Assert.Equal("/work", invocation.Environment[0].Value);
        Assert.Null(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("other", [], [])));
    }

    [Theory]
    [InlineData(".", "7\n", "", 0)]
    [InlineData("missing_function", "", "jq: unsupported function missing_function\n", 5)]
    public async Task ExecutionUsesCallerDescriptorsWithoutClosingThem(
        string filter, string expectedOutput, string expectedError, int expectedExit)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("7");
        var output = new JqFileDescriptor(10);
        var error = new JqFileDescriptor(11);
        var invocation = new JqCommandInvocation("jq", [filter], [], JqPath.Root,
            JqFileDescriptor.StdIn, output, error);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(expectedExit, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expectedOutput, host.GetOutput(output));
        Assert.Equal(expectedError, host.GetOutput(error));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.ClosedDescriptors);
    }

    [Fact]
    public async Task FileOperandsPreserveLiteralPathCharacters()
    {
        var host = new MockFileSystem();
        host.AddFile("/work/a:b\\c*", "42");
        var invocation = JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", [".", "./a:b\\c*"], [new JqEnvironmentVariable("PWD", "/work")]);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("42\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task OwnedDescriptorCloseRetriesReportedFailure()
    {
        var host = new MockFileSystem { CloseFailuresRemaining = 1 };
        host.AddFile("/input", "42");
        var command = Assert.IsType<Jq>(Jq.TryParse(
            JqCommandInvocation.CreateWithStandardDescriptors("jq", [".", "/input"], [])));

        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(2, host.ClosedDescriptors.Count);
        Assert.Equal(host.ClosedDescriptors[0], host.ClosedDescriptors[1]);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task CleanupFailurePreservesOriginalCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem { CloseFailuresRemaining = 2 };
        host.AddFile("/input", "42");
        host.BeforeByteRead = _ => cancellation.Cancel();
        var command = Assert.IsType<Jq>(Jq.TryParse(
            JqCommandInvocation.CreateWithStandardDescriptors("jq", [".", "/input"], [])));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, cancellation.Token));
        Assert.Equal(2, host.ClosedDescriptors.Count);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
