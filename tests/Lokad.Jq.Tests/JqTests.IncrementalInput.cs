using System.Globalization;
using System.Text.Json;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public async Task Jq_InputNumbersWaitForTheirCompleteToken(int chunk)
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("12345 -0 1.25e+3 9007199254740993\n");
        var (exit, stdout, stderr) = await RunInputAsync(new ChunkedHost(inner, chunk), "-c", ".");

        Assert.Equal(0, exit);
        Assert.Equal("12345\n-0\n1250\n9007199254740993\n", stdout);
        Assert.Empty(stderr);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    public async Task Jq_InputRetainsNestedValuesAndDuplicateKeyOrder(int chunk)
    {
        var inner = new MockFileSystem();
        inner.SetStandardInput("{\"b\":[{\"x\":null}],\"a\":\"é🚀\\u0061\",\"b\":{\"x\":[false,true,{}]}}\n[[],null]\n");
        var (exit, stdout, stderr) = await RunInputAsync(new ChunkedHost(inner, chunk), "-c", ".");

        Assert.Equal(0, exit);
        Assert.Equal("{\"b\":{\"x\":[false,true,{}]},\"a\":\"é\\ud83d\\ude80a\"}\n[[],null]\n", stdout);
        Assert.Empty(stderr);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(8192)]
    public async Task Jq_InputNodeAllowanceDoesNotDependOnReadFragmentation(int chunk)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/data", JsonSerializer.Serialize(Enumerable.Range(0, 1024).Select(i => new { id = i })));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "length", "/data")));

        int exit = await command.ExecuteAsync(new ChunkedHost(inner, chunk),
            new JqExecutionPolicy { MaximumValueNodes = 4096 }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("1024\n", inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
    }

    [Theory]
    [InlineData("[1,]")]
    [InlineData("{\n\"a\": [1,\n2}")]
    [InlineData("{\"a\":\"\\ud800\"}")]
    [InlineData("[1,2")]
    [InlineData("1e+")]
    [InlineData("{\"a\":nan,\"b\":[2,]}")]
    public async Task Jq_InputFragmentationPreservesMalformedDiagnostics(string malformed)
    {
        var whole = new MockFileSystem();
        whole.SetStandardInput("1\n" + malformed);
        var expected = await RunInputAsync(whole, "-c", ".");
        Assert.Equal(5, expected.Exit);
        Assert.Equal("1\n", expected.Out);

        foreach (int chunk in new[] { 1, 3, 17 })
        {
            var inner = new MockFileSystem();
            inner.SetStandardInput("1\n" + malformed);
            Assert.Equal(expected, await RunInputAsync(new ChunkedHost(inner, chunk), "-c", "."));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(31)]
    public async Task Jq_InputQuotaStopsAnIncompleteTreeAndClosesTheSource(int chunk)
    {
        var inner = new MockFileSystem();
        inner.AddFile("/first", "[" + string.Join(',', Enumerable.Range(0, 1024)
            .Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]");
        inner.AddFile("/later", "0");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "try inputs catch \"caught\"", "/first", "/later")));

        Assert.Equal(5, await command.ExecuteAsync(new ChunkedHost(inner, chunk),
            new JqExecutionPolicy { MaximumValueNodes = 128 }, CancellationToken.None));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: value budget exceeded\n", inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
    }

    [Fact]
    public async Task Jq_InputCancellationDuringAnIncompleteTreeClosesTheSource()
    {
        using var cancellation = new CancellationTokenSource();
        var inner = new MockFileSystem();
        inner.AddFile("/data", "[0,1,2,3,4]");
        inner.BeforeByteRead = _ =>
        {
            if (inner.ReadBytesCallCount == 4)
                cancellation.Cancel();
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/data")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            command.ExecuteAsync(new ChunkedHost(inner, 2), cancellation.Token));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(inner.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(inner.ClosedDescriptors);
        Assert.Equal(0, inner.OpenFileCount);
    }
}
