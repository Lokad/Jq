using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_UnknownFunctionIsCompileError()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "missing_function")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: unsupported function missing_function\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_NestedUnknownFunctionHasNoPartialOutput()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "7 | select(true, missing)")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: unsupported function missing\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("length(1)", "length expects no arguments")]
    [InlineData("empty(1)", "empty expects no arguments")]
    [InlineData("contains", "contains expects one argument")]
    public async Task Jq_WrongArityIsCompileError(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("if true then .")]
    [InlineData("{a:}")]
    public async Task Jq_MalformedFilterIsCompileError(string filter)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_InvalidOptionIsUsageError()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--stream", ".")));

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unsupported option", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_MissingProgramFileIsSystemError()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-f", "/missing.jq")));

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("cannot open", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("--argjson")]
    [InlineData("--jsonargs")]
    public async Task Jq_BadJsonArgumentIsUsageError(string option)
    {
        var host = new MockFileSystem();
        Jq? tool;
        if (option == "--argjson")
        {
            tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "--argjson", "n", "not-json", "$n")));
        }
        else
        {
            tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "$ARGS.positional[]", "--jsonargs", "not-json")));
        }

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a\u0001b")]
    public async Task Jq_InvalidPathOperandIsUsageError(string operand)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", operand)));

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_RuntimeTypeErrorKeepsStageFive()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 | .foo")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("cannot index number", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_CancellationStaysCancellation()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("7");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(host, cancelled.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_CancelledCompileStaysCancellation()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "missing_function")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(host, cancelled.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_HostThrowingIsNotMislabeledAsUserError()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("7");
        host.BeforeByteRead = _ => throw new ArgumentException("bad host buffer");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));

        await Assert.ThrowsAnyAsync<JqHostFailureException>(() => tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_OversizedHostReadIsNotMislabeledAsUserError()
    {
        var host = new OversizedReadHost();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));

        await Assert.ThrowsAnyAsync<JqHostFailureException>(() => tool.ExecuteAsync(host, CancellationToken.None));
    }

    private sealed class OversizedReadHost : IJqHost
    {
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(JqByteReadResult.Success(buffer.Length + 1, false));
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            return Task.FromResult(JqAppendResult.Open);
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken)
        {
            return Task.FromResult(JqOpenedFile.Failure("no files"));
        }

        public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }
}
