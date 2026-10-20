using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[range(3)]", "[0,1,2]\n")]
    [InlineData("[range((0,1);(2,3);(1,2))]", "[0,1,0,0,1,2,0,2,1,1,1,2,1]\n")]
    [InlineData("try [range(false;empty;error(\"unused\"))] catch .", "[]\n")]
    [InlineData("try [range(0;1;false)] catch .", "\"Range bounds must be numeric\"\n")]
    [InlineData("[range(0;3;0),range(0;3;nan),range(0;3;-1)]", "[]\n")]
    [InlineData("[range(3;0;-1)]", "[3,2,1]\n")]
    [InlineData("[range(0.5;2;0.5)]", "[0.5,1,1.5]\n")]
    [InlineData("[range(\"0\";\"2\";\"1\")]", "[0,1]\n")]
    [InlineData("first(range(0;1e100))", "0\n")]
    public async Task Jq_RangeArgumentsRemainOrderedLazyAndNumeric(string filter, string expected)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_RangeArgumentEffectsStayFirstArgumentOuter()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "[range(((0,1)|debug); ((2,3)|debug); (1|debug))]")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[0,1,0,1,2,1,1,2]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("[\"DEBUG:\",0]\n[\"DEBUG:\",2]\n[\"DEBUG:\",1]\n[\"DEBUG:\",3]\n[\"DEBUG:\",1]\n"
            + "[\"DEBUG:\",1]\n[\"DEBUG:\",2]\n[\"DEBUG:\",1]\n[\"DEBUG:\",3]\n[\"DEBUG:\",1]\n",
            host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public void Jq_RangeCancellationStopsAnActiveSequence()
    {
        using var cancellation = new CancellationTokenSource();
        using var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(cancellation.Token));
        var filter = new RangeFilter(new JqFilter[] { new LiteralFilter(JsonValue.Create(1000000)) });
        using IEnumerator<JsonNode?> values = filter.Evaluate(null, context, context.RootEnvironment).GetEnumerator();
        Assert.True(values.MoveNext());
        Assert.Equal(0, values.Current?.GetValue<long>());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => values.MoveNext());
    }
}
