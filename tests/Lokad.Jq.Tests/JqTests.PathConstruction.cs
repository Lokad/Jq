using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("input")]
    [InlineData("deep-input")]
    [InlineData("rawfile")]
    [InlineData("program")]
    [InlineData("library")]
    public async Task Jq_CanonicalPathsAdmitConstructionRatherThanBorrowedCharacters(string kind)
    {
        string path = kind == "deep-input" ? "/" + string.Join('/', Enumerable.Repeat("p", 8192))
            : "/" + new string('p', 16384);
        var host = new MockFileSystem();
        host.AddFile(path, kind == "program" ? "1" : kind == "rawfile" ? "abc" : "0\n");
        string[] arguments = kind switch
        {
            "input" or "deep-input" => ["-c", ".", path],
            "rawfile" => ["-nr", "--rawfile", "value", path, "$value|length"],
            "program" => ["-nc", "-f", path],
            _ => ["-nc", "-L", path, "1"]
        };
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(kind is "input" or "deep-input" ? "0\n" : kind == "rawfile" ? "3\n" : "1\n",
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(kind == "library" ? 0 : 1, host.ReadBytesCallCount);
        Assert.Equal(kind == "library" ? 0 : 1, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("/", "/", "/")]
    [InlineData("/a/b", "/work", "/a/b")]
    [InlineData("a/b", "/work", "/work/a/b")]
    [InlineData("./a//b/../c/", "/work", "/work/a/c")]
    [InlineData("/a/b/../../", "/work", "/")]
    [InlineData("../c", "/work", "/c")]
    public void Jq_PathConstructionKeepsDisplayAndCanonicalPolicy(string raw, string directory, string expected)
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        {
            var result = JqPathResolution.ResolveArgument(raw, new JqPath(directory), budget);
            Assert.Same(raw, result.Display);
            Assert.Equal(expected, result.Absolute.Path);
            if (raw == expected && raw != "/") Assert.Same(raw, result.Absolute.Path);
            if (raw == "/") Assert.Same(JqPath.Root, result.Absolute);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("segments")]
    [InlineData("label")]
    [InlineData("attached-library")]
    public async Task Jq_ActualPathConstructionRefusalIsTerminalBeforeOpening(string kind)
    {
        string directory = kind == "relative" ? "/" + new string('p', 40000) : "/";
        string path = kind == "segments" ? "/" + string.Join('/', Enumerable.Repeat("p", 8192)) + "/."
            : "/" + new string('p', 40000);
        string[] arguments = kind switch
        {
            "relative" => ["-c", ".", "file"],
            "label" => ["-nc", "-f", path],
            "attached-library" => ["-nc", "-L" + path, "1"],
            _ => ["-c", ".", path]
        };
        var host = new MockFileSystem();
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, [new("PWD", directory)]);
        Assert.Equal(5, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_PathConstructionChecksCancellationBeforeAdmission()
    {
        string path = "/" + new string('p', 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var memory = new BenchmarkMemoryBudget(0);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => JqPathResolution.ResolveArgument(path, JqPath.Root, budget));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
