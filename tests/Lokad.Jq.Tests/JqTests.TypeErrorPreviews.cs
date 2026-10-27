using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("contains([])", false, "string (\"xxxxxxxxxxxxxxxxxxxxxxxx...\") and array ([]) cannot have their containment checked")]
    [InlineData("@base64d", false, "string (\"xxxxxxxxxxxxxxxxxxxxxxxx...\") is not valid base64 data")]
    [InlineData("@urid", false, "string (\"xxxxxxxxxxxxxxxxxxxxxxxx...\") is not a valid uri encoding")]
    [InlineData("sort_by(.)", true, "object ({\"payload\":\"xxxxxxxxxxxxx...}) and array ([[\"xxxxxxxxxxxxxxxxxxxxxx...]) cannot be sorted, as they are not both arrays")]
    [InlineData("test(\"x\")", true, "object ({\"payload\":\"xxxxxxxxxxxxx...}) cannot be matched, as it is not a string")]
    [InlineData("_strindices(\"x\")", true, "object ({\"payload\":\"xxxxxxxxxxxxx...}) cannot be searched, as it is not a string")]
    [InlineData("fromjson", true, "object ({\"payload\":\"xxxxxxxxxxxxx...}) only strings can be parsed")]
    [InlineData("keys", false, "string (\"xxxxxxxxxxxxxxxxxxxxxxxx...\") has no keys")]
    [InlineData("bsearch(0)", false, "string (\"xxxxxxxxxxxxxxxxxxxxxxxx...\") cannot be searched from")]
    [InlineData("@csv", true, "object ({\"payload\":\"xxxxxxxxxxxxx...}) cannot be csv-formatted, only array")]
    [InlineData("@sh", true, "object ({\"payload\":\"xxxxxxxxxxxxx...}) can not be escaped for shell")]
    [InlineData("path({payload:.payload})", true, "Invalid path expression with result {\"payload\":\"xxxxxxxxxxxxx...}")]
    [InlineData("gmtime", true, "gmtime requires numeric inputs")]
    [InlineData("localtime", true, "localtime requires numeric inputs")]
    public async Task Jq_TypeErrorsUseBoundedNativeOperandPreviews(string expression, bool objectInput, string expected)
    {
        string text = new('x', 65536);
        if (expression == "@base64d") text += "$";
        if (expression == "@urid") text += "%";
        var host = new MockFileSystem();
        host.SetStandardInput(objectInput ? "{\"payload\":" + JsonSerializer.Serialize(text) + "}" : JsonSerializer.Serialize(text));
        var memory = new BenchmarkMemoryBudget(384 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "try (" + expression + ") catch .")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, JsonSerializer.Deserialize<string>(host.GetOutput(JqFileDescriptor.StdOut)));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("try error catch length", "65536\n")]
    [InlineData("tojson | length", "65538\n")]
    [InlineData("@json | length", "65538\n")]
    [InlineData("tostring | length", "65536\n")]
    public async Task Jq_BoundedTypeErrorsKeepExplicitPayloadAndOutputContents(string expression, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(JsonSerializer.Serialize(new string('x', 65536)));
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", expression)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_BoundedTypeErrorsKeepFullHaltDiagnostics()
    {
        string text = new('x', 65536);
        var host = new MockFileSystem();
        host.SetStandardInput("{\"payload\":\"" + text + "\"}");
        var memory = new BenchmarkMemoryBudget(2 * 1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "halt_error(7)")));
        Assert.Equal(7, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("{\"payload\":\"" + text + "\"}\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }
}
