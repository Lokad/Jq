namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{\"a\":6,\"b\":2}", "[(.a+.b),(.a-.b),(.a*.b),(.a/.b),(.a%.b)]", "[8,4,12,3,0]\n")]
    [InlineData("{\"a\":6,\"b\":2}", "[(.a==.b),(.a!=.b),(.a<.b),(.a<=.b),(.a>.b),(.a>=.b)]", "[false,true,false,false,true,true]\n")]
    [InlineData("{\"a\":false,\"b\":1}", ".a and .b.bad", "false\n")]
    [InlineData("{\"a\":true,\"b\":1}", ".a or .b.bad", "true\n")]
    [InlineData("{\"a\":null,\"b\":0}", ".a or .b", "true\n")]
    [InlineData("{\"a\":0,\"b\":null}", ".a and .b", "false\n")]
    [InlineData("{\"a\":2,\"b\":3}", "(.a,.b)+(.a,.b)", "4\n5\n5\n6\n")]
    public async Task Jq_SingleValueOperatorsPreserveResultsAndShortCircuiting(string input, string filter, string output)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal(output, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
