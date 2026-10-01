using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"error message\"", "try error catch .", "\"error message\"\n")]
    [InlineData("42", "try error(\"invalid value: \\(.)\") catch .", "\"invalid value: 42\"\n")]
    [InlineData("true", "try error(\"some exception\") catch .", "\"some exception\"\n")]
    [InlineData("[{}, true, {\"a\": 1}]", "[.[] | try .a]", "[\n  null,\n  1\n]\n")]
    [InlineData("5", "try error(\"e\") catch .", "\"e\"\n")]
    [InlineData("0", "try (try error(\"a\") catch error(\"b\")) catch .", "\"b\"\n")]
    [InlineData("0", "def f: try error(\"e\") catch .; f", "\"e\"\n")]
    [InlineData("1", "try (1 as $x | $x | .a) catch \"caught\"", "\"caught\"\n")]
    [InlineData("\"foo\"", "try ((try . catch \"caught too much\") | error) catch \"caught just right\"", "\"caught just right\"\n")]
    [InlineData("[\"hi\", \"ho\"]", ".[] | (try (if . == \"hi\" then . else error end) catch empty) | \"\\(.) there!\"", "\"hi there!\"\n")]
    [InlineData("null", "try ([\"hi\", \"ho\"] | .[] | (try . catch (if . == \"ho\" then \"BROKEN\" | error else empty end)) | if . == \"ho\" then error else \"\\(.) there!\" end) catch \"caught outside \\(.)\"", "\"hi there!\"\n\"caught outside ho\"\n")]
    [InlineData("\"foo\"", "try (try error catch \"inner catch \\(.)\") catch \"outer catch \\(.)\"", "\"inner catch foo\"\n")]
    [InlineData("\"foo\"", "try ((try error catch \"inner catch \\(.)\") | error) catch \"outer catch \\(.)\"", "\"outer catch inner catch foo\"\n")]
    // Like the reference body-level `//`, the error propagates through the
    // right branch and the suppressed run emits nothing.
    [InlineData("null", "try error(0) // 1", "")]
    [InlineData("null", "try to_entries catch .", "\"null (null) has no keys\"\n")]
    [InlineData("null", "try error(\"\\($__loc__)\") catch .", "\"{\\\"file\\\":\\\"<top-level>\\\",\\\"line\\\":1}\"\n")]
    [InlineData("null", "1 + try 2 catch 3 + 4", "7\n")]
    [InlineData("null", "{x: try 1, y: try error catch 2, z: if true then 3 end}", "{\n  \"x\": 1,\n  \"y\": 2,\n  \"z\": 3\n}\n")]
    [InlineData("[1,null,2]", ".[] | try error catch .", "1\nnull\n2\n")]
    [InlineData("[\"hi\",\"ho\"]", ".[]|(try (if .==\"hi\" then . else error end) catch empty) | \"\\(.) there!\"", "\"hi there!\"\n")]
    [InlineData("true", "try .a catch \". is not an object\"", "\". is not an object\"\n")]
    [InlineData("\"foo\"", "[if error then 1 else 2 end?]", "[]\n")]
    [InlineData("null", "1, try error(2), 3", "1\n3\n")]
    // Upstream skips pending comma alternatives once the body raises
    // (execute.c ON_BACKTRACK(FORK) backtracks while raising): the first
    // error aborts the body, earlier values are kept, the handler runs once.
    [InlineData("null", "try (1, error(\"x\")) catch 99", "1\n99\n")]
    [InlineData("null", "try (1, error(\"x\"), 2) catch 99", "1\n99\n")]
    [InlineData("null", "try (error(\"x\"), 1) catch 99", "99\n")]
    [InlineData("null", "try (error(\"x\"), error(\"y\")) catch .", "\"x\"\n")]
    [InlineData("null", "try (1, 2) catch 99", "1\n2\n")]
    [InlineData("1", "[-try .]", "[\n  -1\n]\n")]
    [InlineData("[\"a\",1,2,3,4,5,6,7]", "try mktime catch .", "\"mktime requires parsed datetime inputs\"\n")]
    [InlineData("0", "try (1/.) catch .", "\"number (1) and number (0) cannot be divided because the divisor is zero\"\n")]
    [InlineData("0", "try (1/0) catch .", "\"number (1) and number (0) cannot be divided because the divisor is zero\"\n")]
    [InlineData("0", "try (0/0) catch .", "\"number (0) and number (0) cannot be divided because the divisor is zero\"\n")]
    [InlineData("0", "try (1%.) catch .", "\"number (1) and number (0) cannot be divided (remainder) because the divisor is zero\"\n")]
    [InlineData("0", "try (1%0) catch .", "\"number (1) and number (0) cannot be divided (remainder) because the divisor is zero\"\n")]
    [InlineData("1", ". |= try . catch .", "1\n")]
    [InlineData("null", "\"foo\" | try ((try . catch \"caught too much\") | error) catch \"caught just right\"", "\"caught just right\"\n")]
    // Upstream try/catch/general-`?` vector: explicit errors, index mistypes, and empty all behave per the reference; the index wording follows the structured-error policy.
    [InlineData("[0,1,2,3]", "[.[]|try if . == 0 then error(\"foo\") elif . == 1 then .a elif . == 2 then empty else . end catch .]", "[\n  \"foo\",\n  \"cannot index number with string \\\"a\\\"\",\n  3\n]\n")]
    public async Task Jq_TryCatchHandlesErrors(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_AndOrPropagateDecisiveErrors()
    {
        // Short-circuiting skips only unevaluated branches; errors on the taken path propagate.
        foreach (var filter in new string[]
        {
            "true and error(\"x\")",
            "false or error(\"x\")",
        })
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
            Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal("jq: error (at <unknown>): x\n", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        }
    }

    [Fact]
    public async Task Jq_TryCatchObservesPayloads()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[{\"a\": [1, 2]}, {\"a\": 123}]");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "[.[] | [try .a[] catch ., try .a.[] catch ., .a[]?, .a.[]?]]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  [\n    1,\n    2,\n    1,\n    2,\n    1,\n    2,\n    1,\n    2\n  ],\n  [\n    \"cannot iterate over number\",\n    \"cannot iterate over number\"\n  ]\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TryDiscardsPartialOutputs()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("{\"a\": [\"b\"], \"c\": [\"d\"]}");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "try [\"OK\", (.[] | error)] catch [\"KO\", .]")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("[\n  \"KO\",\n  [\n    \"b\"\n  ]\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("error(\"boom\")", "jq: error (at <stdin>:1): boom\n")]
    [InlineData("\"x\" | error", "jq: error (at <stdin>:1): x\n")]
    [InlineData("[1] | error", "jq: error (at <stdin>:1) (not a string): [1]\n")]
    public async Task Jq_UncaughtErrorsFailWithPayload(string filter, string expectedError)
    {
        var host = new MockFileSystem();
        host.SetStandardInput("[1]\n");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expectedError, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_UncaughtErrorsReportFailingInputPosition()
    {
        // The position tracks the failing input like the reference: earlier
        // outputs are kept with sticky exit 5, and explicit reads under -n
        // resolve <stdin> instead of <unknown>.
        foreach (var (stdin, args, exit, stdout, stderr) in new (string, string[], int, string, string)[]
        {
            ("1\n2\n", new string[] { "if . == 2 then error(\"x\") else . end" }, 5, "1\n", "jq: error (at <stdin>:2): x\n"),
            ("5\n", new string[] { "-n", "[input] | error(\"x\")" }, 5, "", "jq: error (at <stdin>:1): x\n"),
        })
        {
            var host = new MockFileSystem();
            host.SetStandardInput(stdin);
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", args)));
            Assert.Equal(exit, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(stdout, host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(stderr, host.GetOutput(JqFileDescriptor.StdErr));
        }
    }

    [Fact]
    public async Task Jq_EmptyErrorArgumentYieldsNothing()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "error(empty)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TryCannotNeutralizeQuota()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try (def r: r; r) catch 42")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_TryCannotNeutralizeCancellation()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try [range(0; 100)] catch 42")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => tool.ExecuteAsync(host, cancelled.Token));
    }

    [Theory]
    [InlineData("false and error(\"x\")", "false\n")]
    [InlineData("true and (true, false)", "true\nfalse\n")]
    [InlineData("(true, false) and true", "true\nfalse\n")]
    [InlineData("true or error(\"x\")", "true\n")]
    [InlineData("false or (true, false)", "true\nfalse\n")]
    [InlineData("(false, false) or (true, false)", "true\nfalse\ntrue\nfalse\n")]
    [InlineData("(true, false) or false", "true\nfalse\n")]
    [InlineData("(true, true) and (true, false)", "true\nfalse\ntrue\nfalse\n")]
    [InlineData("[[true,[]], [false,1], [42,null], [null,false]] | .[] | [.[0] and .[1], .[0] or .[1]]", "[\n  true,\n  true\n]\n[\n  false,\n  true\n]\n[\n  false,\n  true\n]\n[\n  false,\n  false\n]\n")]
    [InlineData("{} | [10 > 0, 10 > 10, 10 > 20, 10 < 0, 10 < 10, 10 < 20]", "[\n  true,\n  false,\n  false,\n  false,\n  false,\n  true\n]\n")]
    [InlineData("1 and 2", "true\n")]
    [InlineData("empty and true", "")]
    [InlineData("42 and \"a string\"", "true\n")]
    public async Task Jq_AndOrShortCircuitOverGenerators(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_CatchHandlerErrorsPropagateAfterPrefix()
    {
        // An error raised inside a catch handler is not recaught: earlier
        // inputs keep their outputs while the run still reports status 5.
        var host = new MockFileSystem();
        host.SetStandardInput("[\"hi\",\"ho\"]\n");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".[]|(try . catch (if .==\"ho\" then \"BROKEN\"|error else empty end)) | if .==\"ho\" then error else \"\\(.) there!\" end")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"hi there!\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: error (at <stdin>:1): ho\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TryBodyTailCallsStayFlat()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def f($n): if $n == 0 then 0 else try f($n - 1) catch -1 end; f(5000)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_TryBindsTightly()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try error(\"x\") | 1 catch 2")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("expected End, got catch", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("0", "halt", 0, "", "")]
    [InlineData("0", "1, halt", 0, "1\n", "")]
    [InlineData("0", "halt, 1", 0, "", "")]
    [InlineData("null", "halt_error(11)", 11, "", "")]
    [InlineData("null", "halt_error", 5, "", "")]
    [InlineData("null", "1, halt_error(3)", 3, "1\n", "")]
    [InlineData("\"xy\"", "halt_error(1)", 1, "", "xy")]
    [InlineData("{\"a\": \"xyz\"}", "halt_error(1)", 1, "", "{\"a\":\"xyz\"}\n")]
    [InlineData("null", "\"x\\u0000y\\u0000z\" | halt_error(1)", 1, "", "x\0y\0z")]
    public async Task Jq_HaltTerminatesImmediately(string input, string filter, int exitCode, string expectedOut, string expectedErr)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(exitCode, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expectedOut, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(expectedErr, host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_HaltStopsLaterInputs()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("1\n2\n");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "halt")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_HaltEscapesTry()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try halt catch 42")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_HaltErrorRejectsNonNumbers()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "halt_error(\"x\")")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("halt_error requires a numeric exit code", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("[0, 1, 2]", "[(label $here | .[] | if . > 1 then break $here else . end), \"hi!\"]", "[\n  0,\n  1,\n  \"hi!\"\n]\n")]
    [InlineData("[0, 2, 1]", "[(label $here | .[] | if . > 1 then break $here else . end), \"hi!\"]", "[\n  0,\n  \"hi!\"\n]\n")]
    [InlineData("0", "[label $o | (label $i | (1, break $i)), 2]", "[\n  1,\n  2\n]\n")]
    [InlineData("0", "[label $o | (label $i | (1, break $o)), 2]", "[\n  1\n]\n")]
    [InlineData("0", "5 | label $x | (1 | break $x)", "")]
    [InlineData("0", "[label $o | ((1, break $o)?), 2]", "[\n  1\n]\n")]
    [InlineData("0", "[label $o | (try break $o catch 42)]", "[]\n")]
    [InlineData("0", "label $x | def f: break $x; f", "")]
    public async Task Jq_LabelBreakAbandonsCleanly(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_UnknownLabelFailsAtCompile()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", ". as $foo | break $foo")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("undefined label $foo", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_LabelBodyTailCallsStayFlat()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "def f($n): if $n == 0 then 0 else label $l | f($n - 1) end; f(5000)")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
}
