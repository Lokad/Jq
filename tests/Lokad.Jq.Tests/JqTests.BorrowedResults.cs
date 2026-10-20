using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("values")]
    [InlineData("abs")]
    [InlineData("debug(empty)")]
    [InlineData("debug")]
    [InlineData("stderr")]
    [InlineData(". + null")]
    [InlineData("null + .")]
    public async Task Jq_PassThroughBuiltinsKeepBorrowedAliases(string operation)
    {
        foreach (string input in new[] { "[1,2]", "{\"a\":1}", "\"hello\"" })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(input);
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "(" + operation + ") as $x | path($x)")));
            Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal("[]\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(operation == "debug" ? "[\"DEBUG:\"," + input + "]\n"
                : operation == "stderr" ? input == "\"hello\"" ? "hello" : input : "", host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Theory]
    [InlineData("arrays", "[1,2]")]
    [InlineData("iterables", "[1,2]")]
    [InlineData("objects", "{\"a\":1}")]
    [InlineData("iterables", "{\"a\":1}")]
    [InlineData("strings", "\"hello\"")]
    [InlineData("abs", "[1,2]")]
    [InlineData("abs", "0")]
    public async Task Jq_PassThroughBuiltinsRetainDirectPathTracking(string operation, string input)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "path(" + operation + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("min_by(.)")]
    [InlineData("max_by(.)")]
    [InlineData("_min_by_impl([0])")]
    [InlineData("_max_by_impl([0])")]
    [InlineData("add")]
    public async Task Jq_ExtremaAndSingletonAddKeepSelectedAliases(string operation)
    {
        foreach (string value in new[] { "[1,2]", "{\"a\":1}", "\"hello\"" })
        {
            var host = new MockFileSystem();
            host.SetStandardInput("[" + value + "]");
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", operation + " as $x | .[0] | path($x)")));
            Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal("[]\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Theory]
    [InlineData("arrays")]
    [InlineData("iterables")]
    [InlineData("values")]
    [InlineData("abs")]
    [InlineData("debug(empty)")]
    [InlineData(". + null")]
    [InlineData("null + .")]
    public void Jq_PassThroughBuiltinsBorrowLargeValuesWithoutAdmission(string source)
    {
        var input = new JsonArray();
        for (int index = 0; index < 10000; index++) input.Add(index);
        var memory = new BenchmarkMemoryBudget(0);
        using var lexical = new JqBudget(CancellationToken.None);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None)))
        {
            JqFilter filter = new JqParser(source, JqProgramSource.Inline, context.RootEnvironment, lexical).Parse();
            Assert.Same(input, filter.Evaluate(input, context, context.RootEnvironment).Single());
            Assert.Equal(0, memory.PeakBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("-1", "try path(abs) catch \"changed\"", "\"changed\"\n")]
    [InlineData("[1,2]", ". as $x | (values | .[0] = 3), $x", "[3,2]\n[1,2]\n")]
    [InlineData("[[1,2]]", ". as $x | (min | .[0] = 3), $x", "[3,2]\n[[1,2]]\n")]
    [InlineData("[1,2]", ". as $x | ((. + null) | .[0] = 3), $x", "[3,2]\n[1,2]\n")]
    [InlineData("null", "[arrays,objects,values]", "[]\n")]
    public async Task Jq_BorrowedBuiltinResultsKeepComputedPathsAndWritesIndependent(string input, string source, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
