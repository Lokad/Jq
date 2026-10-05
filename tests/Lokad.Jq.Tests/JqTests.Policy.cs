using System.Threading;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"x\"", "test(\"x\" * 16385)", "regex pattern exceeds")]
    [InlineData("\"x\"", "test(\"(\" * 65 + \"x\" + \")\" * 65)", "invalid regex")]
    [InlineData("\"a\" * 1000 + \"!\"", "test(\"(*NO_START_OPT)(*NO_AUTO_POSSESS)(a+)+$\")", "regex matching failed")]
    [InlineData("\"a\" * 6000", "test(\"(*NO_START_OPT)(*NO_AUTO_POSSESS)a.*b\")", "regex work limit exceeded")]
    public async Task Jq_RegexQuotasBypassLanguageHandlers(string input, string expression, string diagnostic)
    {
        foreach (string handler in new[] { "try (" + expression + ") catch \"caught\"", "(" + expression + ")?", "(" + expression + ")? // \"fallback\"" })
        {
            var host = new MockFileSystem();
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", input + " | " + handler)));

            Assert.Equal(5, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
            Assert.StartsWith("jq: ", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
            Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Jq_RegexQuotaStopsLaterInputsAndClosesOwnedFiles()
    {
        var host = new MockFileSystem();
        host.AddFile("/first", "\"ok\"\n\"" + new string('a', 6000) + "\"\n\"ok\"\n");
        host.AddFile("/later", "\"ok\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq",
            "if . == \"ok\" then . else try test(\"(*NO_START_OPT)(*NO_AUTO_POSSESS)a.*b\") catch \"caught\" end",
            "/first", "/later")));

        Assert.Equal(5, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"ok\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: regex work limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_InvalidRegexSyntaxRemainsCatchable()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"x\" | try test(\"[\") catch \"caught\"")));

        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"caught\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
