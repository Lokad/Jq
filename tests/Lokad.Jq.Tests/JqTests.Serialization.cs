using System.Text;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public async Task Jq_SerializationPreservesLiteralUnicodeEscapeSpelling(int slashes, bool ascii)
    {
        string json = "\"" + new string('\\', 2 * slashes) + "uABCD\\\"quote\"";
        string expected = "\"" + new string('\\', 2 * slashes)
            + (ascii ? "uABCD\\u0022quote\"\n" : "uABCD\\\"quote\"\n");
        foreach (string filter in new[] { ".", "[.] | tostring | fromjson | .[0]", "tojson | fromjson" })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(json);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ascii ? "-ac" : "-c", filter)));

            Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(Encoding.UTF8.GetBytes(expected), host.GetOutputBytes(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
        }
    }

    [Fact]
    public async Task Jq_SerializationLowercasesRealEscapesBesideLiteralEscapesAndQuotes()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("\"\\\\uABCD\\\"\\uABCD\\u001F\\ud83d\\ude80\"");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-ac", ".")));

        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"\\\\uABCD\\u0022\\uabcd\\u001f\\ud83d\\ude80\"\n"u8.ToArray(), host.GetOutputBytes(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutputBytes(JqFileDescriptor.StdErr));
    }
}
