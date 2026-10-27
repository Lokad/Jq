using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("try bsearch(empty) catch .", "null", "", "", 0)]
    [InlineData("try bsearch((debug|1)) catch .", "null", "\"null (null) cannot be searched from\"\n", "[\"DEBUG:\",null]\n", 0)]
    [InlineData("try bsearch(error(\"arg\")) catch .", "null", "\"arg\"\n", "", 0)]
    [InlineData("try sub(empty; \"x\") catch .", "null", "", "", 0)]
    [InlineData("try sub((debug|\"a\"); \"x\") catch .", "null", "\"null (null) cannot be matched, as it is not a string\"\n", "[\"DEBUG:\",null]\n", 0)]
    [InlineData("try sub(error(\"pattern\"); \"x\") catch .", "null", "\"pattern\"\n", "", 0)]
    [InlineData("try sub(\"a\"; \"x\"; empty) catch .", "null", "", "", 0)]
    [InlineData("try sub(\"a\"; \"x\"; (debug|\"\")) catch .", "null", "\"null (null) cannot be matched, as it is not a string\"\n", "[\"DEBUG:\",null]\n", 0)]
    [InlineData("try sub(\"a\"; \"x\"; error(\"flags\")) catch .", "null", "\"flags\"\n", "", 0)]
    [InlineData("try sub(\"a\"; (debug|\"x\")) catch .", "null", "\"null (null) cannot be matched, as it is not a string\"\n", "", 0)]
    [InlineData("try sub(\"a\"; error(\"replacement\")) catch .", "null", "\"null (null) cannot be matched, as it is not a string\"\n", "", 0)]
    [InlineData("try gsub(empty; \"x\") catch .", "null", "", "", 0)]
    [InlineData("try gsub((debug|\"a\"); \"x\") catch .", "null", "\"null (null) cannot be matched, as it is not a string\"\n", "[\"DEBUG:\",null]\n", 0)]
    [InlineData("try gsub(error(\"pattern\"); \"x\") catch .", "null", "\"pattern\"\n", "", 0)]
    [InlineData("try gsub(\"a\"; \"x\"; empty) catch .", "null", "", "", 0)]
    [InlineData("try gsub(\"a\"; \"x\"; (debug|\"\")) catch .", "null", "\"null (null) cannot be matched, as it is not a string\"\n", "[\"DEBUG:\",null]\n", 0)]
    [InlineData("try gsub(\"a\"; \"x\"; error(\"flags\")) catch .", "null", "\"flags\"\n", "", 0)]
    [InlineData("try gsub(\"a\"; (debug|\"x\")) catch .", "null", "\"null (null) cannot be matched, as it is not a string\"\n", "", 0)]
    [InlineData("try gsub(\"a\"; error(\"replacement\")) catch .", "null", "\"null (null) cannot be matched, as it is not a string\"\n", "", 0)]
    [InlineData("try bsearch(halt) catch .", "null", "", "", 0)]
    [InlineData("try bsearch(halt_error(7)) catch .", "null", "", "", 7)]
    [InlineData("try bsearch(input) catch .", "null\n\"a\"", "\"null (null) cannot be searched from\"\n", "", 0)]
    [InlineData("try sub(halt; \"x\") catch .", "null", "", "", 0)]
    [InlineData("try sub(halt_error(7); \"x\") catch .", "null", "", "", 7)]
    [InlineData("try sub(input; \"x\") catch .", "null\n\"a\"", "\"null (null) cannot be matched, as it is not a string\"\n", "", 0)]
    [InlineData("try gsub(halt; \"x\") catch .", "null", "", "", 0)]
    [InlineData("try gsub(halt_error(7); \"x\") catch .", "null", "", "", 7)]
    [InlineData("try gsub(input; \"x\") catch .", "null\n\"a\"", "\"null (null) cannot be matched, as it is not a string\"\n", "", 0)]
    public async Task Jq_ValueArgumentsRunBeforeInputValidation(string filter, string input,
        string expectedOutput, string expectedError, int status)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/records", input);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", filter, "/records")));
        Assert.Equal(status, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expectedOutput, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(expectedError, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("bsearch(\"x\" * 100000)")]
    [InlineData("sub(\"x\" * 100000; \"replacement\")")]
    [InlineData("gsub(\"x\"; \"replacement\"; \"x\" * 100000)")]
    public async Task Jq_ValueArgumentAdmissionRefusalPrecedesInvalidInput(string expression)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nr",
            "\"ready\", (try " + expression + " catch \"caught\")")));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }
}
