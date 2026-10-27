using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class CoreRegexBoundaryTests
{
    [Theory]
    [InlineData("[range(3)]", "[0,1,2]\n")]
    [InlineData("if false then test(\"x\") else 42 end", "42\n")]
    [InlineData("try test(\"x\") catch .", "\"regex engine is not configured\"\n")]
    [InlineData("test(\"x\")?", "")]
    [InlineData("try match(\"x\") catch .", "\"regex engine is not configured\"\n")]
    public async Task NoProviderSupportsOrdinaryFiltersAndReportsOnlyExecutedRegex(string filter, string expected)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        Assert.True(JqCommandInvocation.TryCreateWithStandardDescriptors("jq", ["-nc", "\"x\"|(" + filter + ")"], [], memory, out var invocation));
        using (invocation)
        {
            var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
            Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Fact]
    public async Task UnconfiguredRegexPreservesEachPrefixAndClosesOwnedInputs()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/first", "\"x\"\n");
        host.AddFile("/later", "\"later\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", ["-r", "\"prefix\",test(\"x\"),\"after\"", "/first", "/later"], [])));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("prefix\nprefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error (at /first:1): regex engine is not configured\n"
            + "jq: error (at /later:1): regex engine is not configured\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task PreCancellationWinsOverMissingProvider()
    {
        using var cancellation = new CancellationTokenSource();
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", ["-n", "\"x\"|test(\"x\")"], [])));
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoProviderStreamsWithinSharedAdmissionAndStopsOnClosure(bool close)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = !close };
        host.SetStandardInput(string.Concat(Enumerable.Repeat("{\"v\":1}\n", 2500)));
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-c", ".v"], [])));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("1\n", close ? 1 : 2500)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(close ? 1 : 2500, host.AppendCallCount);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Fact]
    public async Task NoProviderCancellationPreservesEarlierOutputAndRefunds()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("1\n", 2500)));
        host.BeforeByteAppend = () => { if (host.AppendCallCount == 3) cancellation.Cancel(); };
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-c", ".+1"], [])));
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host, memory, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal("2\n2\n2\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, memory.ActiveScopes);
    }

    [Fact]
    public void CoreAssemblyDoesNotReferenceOrContainAProvider()
    {
        var assembly = typeof(Jq).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name =>
            name.Name is { } text && (text.StartsWith("Lokad.Utf8Regex", StringComparison.Ordinal)
                || text == "System.Text.RegularExpressions"));
        Assert.Null(assembly.GetType("Lokad.Jq.JqUtf8RegexEngine"));
    }
}
