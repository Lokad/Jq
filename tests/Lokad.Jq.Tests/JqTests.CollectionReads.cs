using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("{} | from_entries", "{}\n")]
    [InlineData("{a:{key:\"x\",value:1},b:{Key:\"y\",Value:2}} | from_entries", "{\"x\":1,\"y\":2}\n")]
    [InlineData("{b:{name:\"x\",value:null,Value:3},a:{Name:\"y\"}} | from_entries", "{\"x\":null,\"y\":null}\n")]
    [InlineData("[{key:false,Key:null,name:false,Name:\"x\",value:1}] | from_entries", "{\"x\":1}\n")]
    [InlineData("[{key:\"\",Key:\"ignored\",value:2}] | from_entries", "{\"\":2}\n")]
    [InlineData("[{key:\"a\",value:1},{key:\"b\",value:2},{key:\"a\",value:3}] | from_entries", "{\"a\":3,\"b\":2}\n")]
    [InlineData("try ([null] | from_entries) catch .", "\"Cannot use null (null) as object key\"\n")]
    [InlineData("try ([{Name:false}] | from_entries) catch .", "\"Cannot use boolean (false) as object key\"\n")]
    [InlineData("try ([false] | from_entries) catch .", "\"Cannot index boolean with string (\\\"key\\\")\"\n")]
    [InlineData("try ({a:1} | from_entries) catch .", "\"Cannot index number with string (\\\"key\\\")\"\n")]
    [InlineData("{} | join(1)", "\"\"\n")]
    [InlineData("{a:1} | join(\",\")", "\"1\"\n")]
    [InlineData("{b:\"x\",a:\"y\"} | join(\",\")", "\"x,y\"\n")]
    [InlineData("{a:null,b:false,c:3} | join(\",\")", "\",false,3\"\n")]
    [InlineData("try ({a:\"x\",b:\"y\"} | join(1)) catch .", "\"string (\\\"x\\\") and number (1) cannot be added\"\n")]
    [InlineData("[{key:\"a\",value:{x:1}}] as $x | ($x|from_entries|.a.x=2),$x", "{\"a\":{\"x\":2}}\n[{\"key\":\"a\",\"value\":{\"x\":1}}]\n")]
    public async Task Jq_CollectionReadsAcceptObjectsAndPreserveFieldRules(string filter, string expected)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("if .key == \"a\" then null else stderr end", 0, "\"Cannot use null (null) as object key\"\n", "{\"key\":\"b\",\"value\":2}")]
    [InlineData("if .key == \"a\" then null else error(\"body\") end", 0, "\"body\"\n", "")]
    [InlineData("if .key == \"a\" then null else halt_error(7) end", 7, "", "{\"key\":\"b\",\"value\":2}\n")]
    public async Task Jq_WithEntriesFinishesBodyEffectsBeforeEntryValidation(string body, int status, string expected, string stderr)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "try ({a:1,b:2} | with_entries(" + body + ")) catch .")));
        Assert.Equal(status, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(stderr, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public void Jq_CollectionObjectReadsCancelBetweenValuesAndPreflightCopies()
    {
        var source = new JsonObject { ["a"] = new JsonObject { ["key"] = "x", ["value"] = new string('x', 4096) } };
        var root = new BenchmarkMemoryBudget(0);
        using var cancellation = new CancellationTokenSource();
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token))
        {
            var runtime = new JqRuntime(budget);
            Assert.True(Assert.Throws<JqQuotaException>(() => runtime.FromEntries(source)).IsMemoryAdmission);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => runtime.FromEntries(source));
            Assert.Throws<OperationCanceledException>(() => runtime.Join(source, JsonValue.Create(",")));
            Assert.Throws<OperationCanceledException>(() => runtime.Join(new JsonObject(), JsonValue.Create(",")));
        }
        Assert.Equal(4096, source["a"]?["value"]?.GetValue<string>().Length);
        Assert.Equal(0, root.ReservedBytes);
    }
}
