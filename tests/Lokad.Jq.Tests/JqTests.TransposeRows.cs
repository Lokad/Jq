using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("try transpose catch .", "[]", "[]\n")]
    [InlineData("try transpose catch .", "[null]", "[]\n")]
    [InlineData("try transpose catch .", "[\"\"]", "[]\n")]
    [InlineData("try transpose catch .", "[0]", "[]\n")]
    [InlineData("try transpose catch .", "[{}]", "[]\n")]
    [InlineData("try transpose catch .", "[[], \"\"]", "[]\n")]
    [InlineData("try transpose catch .", "[[],0]", "[]\n")]
    [InlineData("try transpose catch .", "[null,{}]", "[]\n")]
    [InlineData("try transpose catch .", "[[1],\"\"]", "\"Cannot index string with number (0)\"\n")]
    [InlineData("try transpose catch .", "[[1],0]", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try transpose catch .", "[[1],{}]", "\"Cannot index object with number (0)\"\n")]
    [InlineData("try transpose catch .", "[{\"0\":1}]", "\"Cannot index object with number (0)\"\n")]
    [InlineData("try transpose catch .", "{\"a\":\"\"}", "[]\n")]
    [InlineData("try transpose catch .", "{\"a\":0}", "[]\n")]
    [InlineData("try transpose catch .", "{\"a\":{}}", "[]\n")]
    [InlineData("try transpose catch .", "[true]", "\"boolean (true) has no length\"\n")]
    [InlineData("try transpose catch .", "[false]", "\"boolean (false) has no length\"\n")]
    [InlineData("try transpose catch .", "[1]", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try transpose catch .", "[-1]", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try transpose catch .", "[0.5]", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try transpose catch .", "[[],[1]]", "[[null,1]]\n")]
    [InlineData("try transpose catch .", "[null,[1]]", "[[null,1]]\n")]
    [InlineData("try transpose catch .", "[[1,2],[3]]", "[[1,3],[2,null]]\n")]
    [InlineData("try ([nan] | transpose) catch .", "null", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try ([nan,[1]] | transpose) catch .", "null", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try ([[1],nan] | transpose) catch .", "null", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try ([0,nan] | transpose) catch .", "null", "[]\n")]
    [InlineData("try ([infinite] | transpose) catch .", "null", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try ([[1],infinite] | transpose) catch .", "null", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try ([infinite,true] | transpose) catch .", "null", "\"boolean (true) has no length\"\n")]
    [InlineData("try ([100000000000000000000] | transpose) catch .", "null", "\"Cannot index number with number (0)\"\n")]
    [InlineData("try ([0,\"\",{}] | transpose) catch .", "null", "[]\n")]
    public async Task Jq_TransposeUsesLengthBeforeRequestedNumericIndices(string filter, string input, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }
}
