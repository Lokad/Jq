using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(".a[.b]", "20\n")]
    [InlineData("path(.a[.b])", "[\"a\",1]\n")]
    [InlineData("path(.a[.b]?)", "[\"a\",1]\n")]
    [InlineData(".a[.b] |= .+1", "{\"a\":[10,21,30],\"d\":[40,50,60],\"b\":1,\"c\":2}\n")]
    [InlineData(".a[.b] = 99", "{\"a\":[10,99,30],\"d\":[40,50,60],\"b\":1,\"c\":2}\n")]
    [InlineData("del(.a[.b])", "{\"a\":[10,30],\"d\":[40,50,60],\"b\":1,\"c\":2}\n")]
    [InlineData("path(.a[.b:.c])", "[\"a\",{\"start\":1,\"end\":2}]\n")]
    [InlineData(".a[.b:3] |= reverse", "{\"a\":[10,30,20],\"d\":[40,50,60],\"b\":1,\"c\":2}\n")]
    [InlineData("path((.a,.d)[(1,2)])", "[\"a\",1]\n[\"d\",1]\n[\"a\",2]\n[\"d\",2]\n")]
    [InlineData("path(.a[(\"bad\",0):1]?)", "[\"a\",{\"start\":0,\"end\":1}]\n")]
    [InlineData(".a[(\"bad\",0):1]?", "[10]\n")]
    [InlineData("null|.[\"bad\":1]", "null\n")]
    [InlineData("null|path(.[\"bad\":1])", "[{\"start\":\"bad\",\"end\":1}]\n")]
    public async Task Jq_ComputedPathsUseTheEnclosingInputAndKeyMajorOrder(string source, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":[10,20,30],\"d\":[40,50,60],\"b\":1,\"c\":2}\n");
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("path((.a|debug)[(1|debug)])", "[\"a\",1]\n", "[\"DEBUG:\",1]\n[\"DEBUG:\",[10,20,30]]\n")]
    [InlineData("path((.a|debug)[(0|debug):(1|debug)])", "[\"a\",{\"start\":0,\"end\":1}]\n", "[\"DEBUG:\",0]\n[\"DEBUG:\",1]\n[\"DEBUG:\",[10,20,30]]\n")]
    [InlineData("path((.a|debug)[empty])", "", "")]
    [InlineData("path((.a|debug)[(0|debug):empty])", "", "[\"DEBUG:\",0]\n")]
    [InlineData("path((.a|debug)[(\"bad\"|debug):(1|debug)]?)", "", "[\"DEBUG:\",\"bad\"]\n[\"DEBUG:\",1]\n[\"DEBUG:\",[10,20,30]]\n")]
    [InlineData("first(path((.a|debug)[(1,error(\"late\"))]))", "[\"a\",1]\n", "[\"DEBUG:\",[10,20,30]]\n")]
    public async Task Jq_ComputedPathEffectsRemainOrderedAndLazy(string source, string expected, string diagnostic)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":[10,20,30]}\n");
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(".[error(\"x\")]?", "")]
    [InlineData(".[0,error(\"x\")]?", "1\n")]
    [InlineData(".[1,error(\"x\")]?", "2\n")]
    [InlineData("path(.[error(\"x\")]?)", "")]
    [InlineData("path(.[0,error(\"x\")]?)", "[0]\n")]
    public async Task Jq_OptionalIndexAccessDoesNotSuppressKeyExpressionErrors(string source, string prefix)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput("[1,2]\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(prefix, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error (at <stdin>:1): x\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ComputedSliceAdmissionRefusalIsTerminal(bool path)
    {
        const long allowance = 4 * 1024 * 1024;
        var root = new BenchmarkMemoryBudget(allowance);
        IMemoryReservation? pressure = null;
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\":[" + string.Join(',', Enumerable.Repeat("null", 10000)) + "],\"b\":0,\"c\":10000}\n");
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(root.TryReserve(allowance - root.ReservedBytes, out pressure));
        };
        string read = path ? "path(.a[.b:.c]?)" : ".a[.b:.c]?";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", "\"ready\", (try " + read + " catch \"caught\")")));
        try
        {
            Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
            Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(Assert.IsAssignableFrom<IMemoryReservation>(pressure).Bytes, root.ReservedBytes);
        }
        finally { pressure?.Dispose(); }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ComputedIndexLoopObservesCancellationAndClosesInput()
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "{\"a\":[1,2],\"b\":1}");
        using var cancellation = new CancellationTokenSource();
        host.BeforeByteAppend = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "\"ready\", (while(true; .) | .a[.b] | empty)", "/input")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ComputedPathClosureStopsDeferredKeysAndClosesInput()
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem { AppendRemainsOpen = false };
        host.AddFile("/input", "{\"a\":[1,2],\"b\":1}");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c",
            "path(.a[(.b,error(\"late\"))])", "/input")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("[\"a\",1]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }
}
