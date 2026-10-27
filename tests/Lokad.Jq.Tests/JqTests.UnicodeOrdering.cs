namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("keys[]", "\ue000\n\U00010000\n")]
    [InlineData("keys_unsorted[]", "\U00010000\n\ue000\n")]
    [InlineData("keys | sort | .[]", "\ue000\n\U00010000\n")]
    public async Task Jq_ObjectKeyOrderingUsesUnicodeScalars(string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"\\ud800\\udc00\":2,\"\\ue000\":1}");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-r", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_SortedOutputUsesUnicodeScalarsAtEveryObjectDepth()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"\\ud800\\udc00\":0,\"\\ue000\":{\"\\ud800\\udc00\":2,\"\\ue000\":1}}");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-caS", ".")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("{\"\\ue000\":{\"\\ue000\":1,\"\\ud800\\udc00\":2},\"\\ud800\\udc00\":0}\n",
            host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
