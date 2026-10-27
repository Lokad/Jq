using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"x\\(1 # )\n)\"")]
    [InlineData("\"x\\(1 # (\n)\"")]
    [InlineData("\"x\\(1 # \" ) (\n)\"")]
    [InlineData("\"x\\(1 # \\\n) ignored\n)\"")]
    [InlineData("\"x\\(1 # \\\r\n) ignored\r\n)\"")]
    [InlineData("\"x\\(1 # \\\\\n)\"")]
    [InlineData("\"x\\(1 # \\\\\\\n) ignored\n)\"")]
    [InlineData("\"x\\(\"\\(1 # )\n)\")\"")]
    public async Task Jq_InterpolationCommentsKeepTheirCodeBoundaries(string filter)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("\"x1\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("\"x\\(1 # )\"")]
    [InlineData("\"x\\(\"nested\"")]
    public async Task Jq_UnterminatedInterpolationRemainsACompileError(string filter)
    {
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(3, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Contains("unterminated", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NestedInterpolationProtectsTheCompilerStack()
    {
        string filter = "1";
        for (int index = 0; index < 80; index++) filter = "\"\\(" + filter + ")\"";
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(3, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Contains("filter nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public void Jq_InterpolationBoundaryScanObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, cancellation.Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Lexer.FindInterpolationEnd("# comment\n)", 0,
            new JqSourceSpan(1, 1), JqProgramSource.Inline, budget));
    }
}
