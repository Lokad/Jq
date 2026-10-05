using System.Threading;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    // Exact jq 1.8.2 bytes: duplicate fields retain their first position,
    // generator branches own their objects and early consumers remain lazy.
    [Theory]
    [InlineData("{b:0, a:1, (\"b\",\"c\"):2, d:3}",
        "{\"b\":2,\"a\":1,\"d\":3}\n{\"b\":0,\"a\":1,\"c\":2,\"d\":3}\n")]
    [InlineData("{a:1, (\"a\",\"b\"):(2,3), c:4}",
        "{\"a\":2,\"c\":4}\n{\"a\":3,\"c\":4}\n{\"a\":1,\"b\":2,\"c\":4}\n{\"a\":1,\"b\":3,\"c\":4}\n")]
    [InlineData("{v:[1,2]} as $x | [[{a:$x,b:(0,1)} | .a.v += [3]], $x]",
        "[[{\"a\":{\"v\":[1,2,3]},\"b\":0},{\"a\":{\"v\":[1,2,3]},\"b\":1}],{\"v\":[1,2]}]\n")]
    [InlineData("limit(1; {a:(1,2),b:(3,error(\"must not run\"))})", "{\"a\":1,\"b\":3}\n")]
    [InlineData("try {a:(1,2),b:(3,error(\"boom\"))} catch .", "{\"a\":1,\"b\":3}\n\"boom\"\n")]
    [InlineData("{a:empty,b:error(\"must not run\")}", "")]
    public async Task Jq_ObjectConstructionPreservesBranchesAndLazyFailures(string filter, string expected)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-c", filter)));

        int status = await command.ExecuteAsync(host, CancellationToken.None);

        Assert.Equal(0, status);
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
