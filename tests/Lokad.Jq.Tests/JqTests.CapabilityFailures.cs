using System.Text.Json;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    public static IEnumerable<object[]> CapabilityFaults()
    {
        foreach (string stage in new[] { "clock", "compile", "compiled", "boolean", "detailed", "names" })
        foreach (string kind in new[] { "argument", "overflow", "format", "json" })
        foreach (string handler in new[] { "plain", "try", "optional" })
            yield return [stage, kind, handler];
    }

    [Theory]
    [MemberData(nameof(CapabilityFaults))]
    public async Task Jq_UnexpectedCapabilityFaultEscapesHandlersAndReleasesOwners(string stage, string kind, string handler)
    {
        Exception sentinel = kind switch
        {
            "argument" => new ArgumentException("provider sentinel"),
            "overflow" => new OverflowException("provider sentinel"),
            "format" => new FormatException("provider sentinel"),
            _ => new JsonException("provider sentinel"),
        };
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/first", "\"x\"\n");
        host.AddFile("/later", "\"later\"\n");
        var engine = new ObservedRegexEngine((operation, _, _) => { if (operation == stage) throw sentinel; });
        string expression = stage switch
        {
            "clock" => "now",
            "detailed" or "names" => "match(\"x\")",
            _ => "test(\"x\")",
        };
        expression = handler switch
        {
            "try" => "try (" + expression + ") catch \"caught\"",
            "optional" => "(" + expression + ")?",
            _ => expression,
        };
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-r", "\"prefix\", (" + expression + "), \"after\"", "/first", "/later"], [],
            new JqClock(new FaultingClock(sentinel), TimeZoneInfo.Utc));
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));

        var failure = await Assert.ThrowsAsync<JqHostFailureException>(() =>
            command.ExecuteAsync(host, engine, memory, CancellationToken.None));
        Assert.Same(sentinel, failure.InnerException);
        Assert.Equal("prefix\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(engine.Created, engine.Disposed);
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class FaultingClock(Exception failure) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw failure;
    }
}
