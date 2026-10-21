namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("join(\",\")", 5, "", "jq: error (at <stdin>:1): Cannot iterate over null (null)\n")]
    [InlineData("try join(\",\") catch .", 0, "\"Cannot iterate over null (null)\"\n", "")]
    [InlineData("join(\",\")?", 0, "", "")]
    [InlineData("join(empty)", 0, "", "")]
    [InlineData("try join(error(\"separator\")) catch .", 0, "\"separator\"\n", "")]
    [InlineData("try join(debug) catch .", 0, "\"Cannot iterate over null (null)\"\n", "[\"DEBUG:\",null]\n")]
    public async Task Jq_JoinRejectsNullAfterRequiredSeparatorEffects(string source, int status, string expected, string error)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("null\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(status, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(error, host.GetOutput(JqFileDescriptor.StdErr));
    }
}
