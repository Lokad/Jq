using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"error message\"", "try error catch .", "\"error message\"\n")]
    [InlineData("42", "try error(\"invalid value: \\(.)\") catch .", "\"invalid value: 42\"\n")]
    [InlineData("true", "try error(\"some exception\") catch .", "\"some exception\"\n")]
    [InlineData("[{}, true, {\"a\": 1}]", "[.[] | try .a]", "[null,1]\n")]
    [InlineData("5", "try error(\"e\") catch .", "\"e\"\n")]
    [InlineData("0", "try (try error(\"a\") catch error(\"b\")) catch .", "\"b\"\n")]
    [InlineData("0", "def f: try error(\"e\") catch .; f", "\"e\"\n")]
    [InlineData("1", "try (1 as $x | $x | .a) catch \"caught\"", "\"caught\"\n")]
    public async Task Jq_TryCatchHandlesErrors(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TryCatchObservesPayloads()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[{\"a\": [1, 2]}, {\"a\": 123}]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[.[] | [try .a[] catch ., try .a.[] catch ., .a[]?, .a.[]?]]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[[1,2,1,2,1,2,1,2],[\"cannot iterate over number\",\"cannot iterate over number\"]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TryDiscardsPartialOutputs()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\": [\"b\"], \"c\": [\"d\"]}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "try [\"OK\", (.[] | error)] catch [\"KO\", .]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\"KO\",[\"b\"]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("error(\"boom\")", "jq: error: boom\n")]
    [InlineData("\"x\" | error", "jq: error: x\n")]
    [InlineData("[1] | error", "jq: error: [1]\n")]
    public async Task Jq_UncaughtErrorsFailWithPayload(string filter, string expectedError)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expectedError, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_EmptyErrorArgumentYieldsNothing()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "error(empty)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TryCannotNeutralizeQuota()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try (def r: r; r) catch 42")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_TryCannotNeutralizeCancellation()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try [range(0; 100)] catch 42")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => tool.ExecuteAsync(host, cancelled.Token));
    }

    [Theory]
    [InlineData("false and error(\"x\")", "false\n")]
    [InlineData("true and (true, false)", "true\nfalse\n")]
    [InlineData("(true, false) and true", "true\nfalse\n")]
    [InlineData("true or error(\"x\")", "true\n")]
    [InlineData("false or (true, false)", "true\nfalse\n")]
    [InlineData("(false, false) or (true, false)", "true\nfalse\ntrue\nfalse\n")]
    [InlineData("1 and 2", "true\n")]
    [InlineData("empty and true", "")]
    public async Task Jq_AndOrShortCircuitOverGenerators(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TryBodyTailCallsStayFlat()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def f($n): if $n == 0 then 0 else try f($n - 1) catch -1 end; f(5000)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TryBindsTightly()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try error(\"x\") | 1 catch 2")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("expected End, got catch", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
