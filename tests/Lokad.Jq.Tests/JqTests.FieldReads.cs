namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{\"a\":{\"b\":[1,2]}}", ".a.b", "[1,2]\n")]
    [InlineData("{}", ".a.b", "null\n")]
    [InlineData("{\"a\":{\"x\":1},\"b\":{\"x\":2}}", "(.a,.b).x", "1\n2\n")]
    [InlineData("1", ".a?", "")]
    [InlineData("{\"a\":{\"b\":1}}", "path(.a.b)", "[\"a\",\"b\"]\n")]
    [InlineData("{\"a\":{\"b\":1}}", ".a.b += 2", "{\"a\":{\"b\":3}}\n")]
    [InlineData("{\"a\":{\"b\":1}}", ".a | . as $x | [$x.b, path($x.b)]", "[1,[\"b\"]]\n")]
    [InlineData("{\"a\":{\"b\":[1]}}", ".a as $x | [.a.b, (.a.b += [2]), $x.b]", "[[1],{\"a\":{\"b\":[1,2]}},[1]]\n")]
    [InlineData("{\"a\":{\"b\":7},\"key\":\"b\"}", ".a[.key]", "7\n")]
    [InlineData("{\"a\":[3,4],\"key\":-1}", ".a[.key]", "4\n")]
    [InlineData("{\"a\":[3],\"key\":8}", ".a[.key]", "null\n")]
    [InlineData("{\"a\":[0,1,0],\"key\":[0]}", ".a[.key]", "[0,2]\n")]
    [InlineData("{\"a\":{\"b\":1},\"key\":\"b\"}", "path(.a[.key])", "[\"a\",\"b\"]\n")]
    [InlineData("{\"a\":{\"b\":1},\"key\":\"b\"}", ".a[.key] += 2", "{\"a\":{\"b\":3},\"key\":\"b\"}\n")]
    [InlineData("{\"a\":{\"b\":1,\"c\":2}}", ".a[(\"b\",\"c\")]", "1\n2\n")]
    public async Task Jq_StaticFieldReadsPreserveStreamsPathsAndBorrowedValues(string input, string filter, string output)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(output, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
