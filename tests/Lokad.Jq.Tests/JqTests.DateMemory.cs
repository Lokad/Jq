using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Jq_DateCopiesAndCaughtFailuresAdmitBeforeConstruction(bool suffix)
    {
        var variables = new Dictionary<string, JsonNode?>();
        using var compiler = new JqContext(variables, new JqBudget(CancellationToken.None));
        var filter = new JqParser("try strptime(\"%Y-%m-%d\") catch length", JqProgramSource.Inline,
            compiler.RootEnvironment, compiler.Budget).Parse();
        JsonValue input = JsonValue.Create(suffix ? "2024-01-02" + new string(' ', 100000) : new string('x', 100000));
        var memory = new BenchmarkMemoryBudget(0);
        using (var context = new JqContext(variables, new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None)))
        {
            var failure = Assert.Throws<JqQuotaException>(() => filter.Evaluate(input, context, context.RootEnvironment).Single());
            Assert.True(failure.IsMemoryAdmission);
        }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Null(input.Parent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Jq_DateAdmissionFailureStaysTerminalAfterEarlierOutput(bool suffix)
    {
        const long allowance = 8 * 1024 * 1024;
        var memory = new BenchmarkMemoryBudget(allowance);
        var host = new MockFileSystem();
        string text = suffix ? "2024-01-02" + new string(' ', 100000) : new string('x', 100000);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "--arg", "text", text,
            "\"ready\", (try ($text | strptime(\"%Y-%m-%d\")) catch length)")));
        IMemoryReservation? pressure = null;
        host.BeforeByteAppend = () =>
        {
            if (pressure is null) Assert.True(memory.TryReserve(allowance - memory.ReservedBytes - 16384, out pressure));
        };
        try
        {
            Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Equal("\"ready\"\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(pressure?.Bytes, memory.ReservedBytes);
        }
        finally { pressure?.Dispose(); }
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("spaces")]
    [InlineData("zone")]
    public void Jq_DateParsingObservesCancellationBeforeLongScans(string shape)
    {
        string text = shape == "spaces" ? new string(' ', 100000) : new string('x', 100000);
        string format = shape switch { "literal" => text, "spaces" => "%n", _ => "%Z" };
        using var cancellation = new CancellationTokenSource();
        var memory = new BenchmarkMemoryBudget(1024 * 1024);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, memory, cancellation.Token)))
        {
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => JqTime.Strptime(context, JsonValue.Create(text), JsonValue.Create(format)));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }
}
