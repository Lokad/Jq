namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    // jq 1.8.2: pattern keys resolve in the enclosing lexical environment,
    // including when an earlier or nested binding uses the same name.
    [InlineData("\"a\" as $k | {a:1} as {($k):$k} | $k", "1\n")]
    [InlineData("\"a\" as $k | {a:\"b\",b:2} as {a:$k,($k):$v} | [$k,$v]", "[\"b\",\"b\"]\n")]
    [InlineData("\"outer\" as $k | [\"a\",{outer:1,a:2}] as [$k,{($k):$v}] | [$k,$v]", "[\"a\",1]\n")]
    [InlineData("\"a\" as $k | [{a:1},{b:2}] | map(. as {($k):$k} | $k)", "[1,null]\n")]
    [InlineData("\"a\" as $k | reduce range(0;64) as $_ (0; {a:1} as {($k):$k} | .+$k)", "64\n")]
    // Controls: names in the completed pattern/body still shadow enclosing names.
    [InlineData("\"outer\" as $k | [1,2] as [$k,$v] | [$k,$v]", "[1,2]\n")]
    [InlineData("\"a\" as $k | {a:1,b:2} as {a:$k,b:$k} | $k", "2\n")]
    public async Task Jq_ObjectPatternKeysUseTheEnclosingLexicalScope(string filter, string expected)
    {
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
