using System;
using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    private static readonly DateTimeOffset FixedInstant = DateTimeOffset.FromUnixTimeSeconds(1777646400);

    private sealed class FixedClock(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment;
    }

    private static JqCommandInvocation BuildTimedInvocation(params string[] args)
    {
        return JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", args, [], new JqClock(new FixedClock(FixedInstant), TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("-n", "$ENV", "{\n  \"A\": \"1\",\n  \"B\": \"x y\"\n}\n", "A=1,B=x y")]
    [InlineData("-n", "$ENV", "{}\n", "")]
    [InlineData("-n", "$ENV", "{\n  \"A\": \"2\"\n}\n", "A=1,A=2")]
    [InlineData("-n", "env", "{\n  \"A\": \"1\"\n}\n", "A=1")]
    [InlineData("-n", "\"x\" | env | .A", "\"1\"\n", "A=1")]
    [InlineData("-n", "[(env | .A = \"9\"), env]", "[\n  {\n    \"A\": \"9\"\n  },\n  {\n    \"A\": \"1\"\n  }\n]\n", "A=1")]
    [InlineData("-n", "$ENV.PAGER", "\"less\"\n", "PAGER=less")]
    [InlineData("-n", "env.PAGER", "\"less\"\n", "PAGER=less")]
    public async Task Jq_EnvironmentSnapshot(string flag, string filter, string expected, string variables)
    {
        var environment = new List<JqEnvironmentVariable>();
        foreach (string pair in variables.Split(",", StringSplitOptions.RemoveEmptyEntries))
        {
            int mark = pair.IndexOf("=");
            environment.Add(new JqEnvironmentVariable(pair[..mark], pair[(mark + 1)..]));
        }
        var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", [flag, filter], environment);
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_ExplicitArgumentWinsOverEnvironment()
    {
        var invocation = JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", ["--arg", "ENV", "5", "-n", "$ENV"], [new JqEnvironmentVariable("A", "1")]);
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"5\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("now", "1777646400\n")]
    [InlineData("\"x\" | now", "1777646400\n")]
    [InlineData("[range(3) | now] | unique | length", "1\n")]
    [InlineData("now | type", "\"number\"\n")]
    public async Task Jq_NowReadsExplicitClock(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildTimedInvocation("-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NowKeepsFractionalSeconds()
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(1777646400).AddMilliseconds(500);
        var invocation = JqCommandInvocation.CreateWithStandardDescriptors(
            "jq", ["-n", "now"], [], new JqClock(new FixedClock(moment), TimeZoneInfo.Utc));
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("1777646400.5\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NowWithoutClockIsExplicit()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "now")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("explicit host clock", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("\"hi\" | debug", "\"hi\"\n", "[\"DEBUG:\",\"hi\"]\n")]
    [InlineData("1 | debug", "1\n", "[\"DEBUG:\",1]\n")]
    [InlineData("\"hi\" | debug(\"a\", \"b\")", "\"hi\"\n", "[\"DEBUG:\",\"a\"]\n[\"DEBUG:\",\"b\"]\n")]
    public async Task Jq_DebugLogsAndPassesThrough(string filter, string expected, string diagnostics)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(diagnostics, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_StderrMirrorsJoinOutput()
    {
        // Like the reference shell suite, joined output and diagnostics
        // carry the same bytes when stderr renders every value.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-c", "-j", "\"hello\\nworld\", null, [false, 0], {\"foo\":[\"bar\"]}, \"\\n\" | stderr")));
        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("hello\nworldnull[false,0]{\"foo\":[\"bar\"]}\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("hello\nworldnull[false,0]{\"foo\":[\"bar\"]}\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_DebugAndStderrFollowOutputModes()
    {
        // Like the reference callbacks, debug rendering follows compactness,
        // ascii escaping, and key sorting while stderr values stay raw JSON;
        // tostring likewise ignores both modes. Hex escapes render lowercase
        // like the reference.
        foreach (var (args, exit, stdout, stderr) in new (string[], int, string, string)[]
        {
            (new string[] { "-n", "-a", "\"\u00E9\" | debug" }, 0, "\"\\u00e9\"\n", "[\"DEBUG:\",\"\\u00e9\"]\n"),
            (new string[] { "-n", "-S", "{\"b\":1,\"a\":2} | debug" }, 0, "{\n  \"a\": 2,\n  \"b\": 1\n}\n", "[\"DEBUG:\",{\"a\":2,\"b\":1}]\n"),
            (new string[] { "-n", "-S", "{\"b\":1,\"a\":2} | tostring" }, 0, "\"{\\\"b\\\":1,\\\"a\\\":2}\"\n", ""),
            (new string[] { "-n", "-a", "{\"x\":\"\u00E9\"} | stderr" }, 0, "{\n  \"x\": \"\\u00e9\"\n}\n", "{\"x\":\"\u00e9\"}"),
            (new string[] { "-n", "-S", "{\"b\":1,\"a\":2} | stderr" }, 0, "{\n  \"a\": 2,\n  \"b\": 1\n}\n", "{\"b\":1,\"a\":2}"),
        })
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", args)));
            Assert.Equal(exit, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(stdout, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(stderr, host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Fact]
    public async Task Jq_SeqAndJoinLeaveDiagnosticsUnframed()
    {
        // Like the reference callbacks, sequence framing and join treatment
        // apply to stdout values only; debug lines, stderr payloads, and halt
        // payloads keep their unframed bytes.
        foreach (var (args, exit, stdout, stderr) in new (string[], int, string, string)[]
        {
            (new string[] { "-n", "--seq", "\"hi\" | debug" }, 0, "\x1E\"hi\"\n", "[\"DEBUG:\",\"hi\"]\n"),
            (new string[] { "-n", "--seq", "\"hi\" | stderr" }, 0, "\x1E\"hi\"\n", "hi"),
            (new string[] { "-n", "--seq", "\"xy\" | halt_error(1)" }, 1, "", "xy"),
            (new string[] { "-n", "-j", "\"hi\" | debug" }, 0, "hi", "[\"DEBUG:\",\"hi\"]\n"),
            (new string[] { "-n", "--seq", "{\"a\":1} | stderr" }, 0, "\x1E{\n  \"a\": 1\n}\n", "{\"a\":1}"),
        })
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", args)));
            Assert.Equal(exit, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(stdout, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(stderr, host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Theory]
    [InlineData("\"hi\" | stderr", "hi\n", "hi")]
    [InlineData("{\"a\":1} | stderr", "{\n  \"a\": 1\n}\n", "{\"a\":1}")]
    [InlineData("1 | stderr | . + 1", "2\n", "1")]
    public async Task Jq_StderrWritesRaw(string filter, string expected, string diagnostics)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(diagnostics, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("\"x\" | debug(\"a\";\"b\")", "expects between 0 and 1 arguments")]
    public async Task Jq_DebugArityIsCompileError(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
