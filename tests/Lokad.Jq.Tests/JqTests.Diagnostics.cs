using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_UnknownFunctionIsCompileError()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "missing_function")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: unsupported function missing_function at line 1 column 1 (filter)\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_NestedUnknownFunctionHasNoPartialOutput()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "7 | select(true, missing)")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: unsupported function missing at line 1 column 18 (filter)\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("length(1)", "length expects no arguments")]
    [InlineData("empty(1)", "empty expects no arguments")]
    [InlineData("contains", "contains expects one argument")]
    [InlineData("pick", "pick expects one argument")]
    [InlineData("stderr(1)", "stderr expects no arguments")]
    [InlineData("input(1)", "input expects no arguments")]
    [InlineData("inputs(1)", "inputs expects no arguments")]
    [InlineData("halt(1)", "halt expects no arguments")]
    [InlineData("getpath()", "getpath expects one argument")]
    [InlineData("setpath([1])", "setpath expects 2 arguments")]
    [InlineData("setpath([1], 1, 2)", "setpath expects 2 arguments")]
    [InlineData("delpaths()", "delpaths expects one argument")]
    [InlineData("del()", "del expects one argument")]
    [InlineData("type(1)", "type expects no arguments")]
    [InlineData("tonumber(1, 2)", "tonumber expects no arguments")]
    public async Task Jq_WrongArityIsCompileError(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# just a comment")]
    [InlineData("def a: .;")]
    [InlineData("def a: .; def b: .;")]

    public async Task Jq_EmptyProgramIsCompileError(string filter)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("Top-level program", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("1\r\n+\r\n2", "3\n")]
    [InlineData("1 # foo\r + 2", "1\n")]
    [InlineData("[\n  1,\n  # foo \\\n  2,\n  # bar \\\\\n  3,\n  4, # baz \\\\\\\n  5, \\\n  6,\n  7\n  # comment \\\n    comment \\\n    comment\n]", "[1,3,4,7]\n")]
    [InlineData("[\r\n1,# comment\r\n2,# comment\\\r\ncomment\r\n3\r\n]", "[1,2,3]\n")]

    public async Task Jq_CommentsAndBreaksLexCleanly(string filter, string expected)
    {
        // CRLF line breaks, carriage-return comments, and backslash
        // continuations (odd trailing runs, across LF and CRLF) lex like
        // the reference IN_COMMENT state.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-c", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }
    [Theory]
    [InlineData("{}\0{}", ".", "{}\n", "parse error")]
    [InlineData("\"\u0001\"", ".", "", "parse error")]
    [InlineData("\"a\n", ".", "", "parse error")]
    [InlineData("foobar", ".", "", "parse error")]
    public async Task Jq_MalformedInputReportsStageFive(string input, string filter, string expectedOut, string diagnostic)
    {
        // Malformed input bytes fail staged even mid-stream, after any
        // values already produced.
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expectedOut, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
    }


    [Theory]
    [InlineData("[")]
    [InlineData("if true then .")]
    [InlineData("{a:}")]
    public async Task Jq_MalformedFilterIsCompileError(string filter)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_InvalidOptionIsUsageError()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "--bogus-flag", ".")));

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unsupported option", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        var dashword = new MockFileSystem();
        var dashwordTool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-infinite | length")));
        Assert.Equal(2, await dashwordTool.ExecuteAsync(dashword, CancellationToken.None));
        Assert.Contains("unsupported option", dashword.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(dashword.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_MissingProgramFileIsSystemError()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-f", "/missing.jq")));

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("cannot open", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("--argjson")]
    [InlineData("--jsonargs")]
    public async Task Jq_BadJsonArgumentIsUsageError(string option)
    {
        var host = new MockFileSystem();
        Jq? tool;
        if (option == "--argjson")
        {
            tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "--argjson", "n", "not-json", "$n")));
        }
        else
        {
            tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "$ARGS.positional[]", "--jsonargs", "not-json")));
        }

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a\u0001b")]
    public async Task Jq_InvalidPathOperandIsUsageError(string operand)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", operand)));

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.StartsWith("jq:", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_RuntimeTypeErrorKeepsStageFive()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1 | .foo")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("cannot index number", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_CancellationStaysCancellation()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("7");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(host, cancelled.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_CancelledCompileStaysCancellation()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "missing_function")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(host, cancelled.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_HostThrowingIsNotMislabeledAsUserError()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("7");
        host.BeforeByteRead = _ => throw new ArgumentException("bad host buffer");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));

        await Assert.ThrowsAnyAsync<JqHostFailureException>(() => tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_OversizedHostReadIsNotMislabeledAsUserError()
    {
        var host = new OversizedReadHost();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));

        await Assert.ThrowsAnyAsync<JqHostFailureException>(() => tool.ExecuteAsync(host, CancellationToken.None));
    }

    [Fact]
    public async Task Jq_CompileErrorReportsSecondLineSpan()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "{\na: missing_function\n}")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: unsupported function missing_function at line 2 column 4 (filter)\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_FileProgramReportsFileSource()
    {
        var host = new MockFileSystem();
        host.AddFile("/filter.jq", "missing_function");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-f", "/filter.jq")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: unsupported function missing_function at line 1 column 1 (file \"/filter.jq\")\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_EmptyFileProgramIsCompileError()
    {
        // The empty-program diagnostic carries the file identity like any
        // other file compile error, complementing the inline pins.
        var host = new MockFileSystem();
        host.AddFile("/filter.jq", "");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-f", "/filter.jq")));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: Top-level program not given (try \".\") at line 1 column 1 (file \"/filter.jq\")\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_BreakEdgesAreCompileErrors()
    {
        // Bare and unbound breaks fail at compile time. The reference reports
        // `break requires a label to break to` and `break used outside labeled
        // control structure` where we report the generic parse and undefined-label shapes.
        foreach (var (filter, diagnostic) in new (string, string)[]
        {
            ("break", "jq: expected $, got <end> at line 1 column 6 (filter)\n"),
            ("break $nosuchlabel", "jq: undefined label $nosuchlabel at line 1 column 7 (filter)\n"),
            ("label $x | break $y", "jq: undefined label $y at line 1 column 18 (filter)\n"),
        })
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
            Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        }
    }

    [Fact]
    public async Task Jq_UndefinedVariableIsCompileError()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "$undefined")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: undefined variable $undefined at line 1 column 1 (filter)\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_FileProgramReportsUndefinedVariableSource()
    {
        var host = new MockFileSystem();
        host.AddFile("/filter.jq", "$undefined");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-f", "/filter.jq")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("jq: undefined variable $undefined at line 1 column 1 (file \"/filter.jq\")\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_NulByteInProgramIsCompileError()
    {
        var host = new MockFileSystem();
        host.AddFile("/filter.jq", ".\x00invalid");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-f", "/filter.jq")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("invalid character", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_DefOnlyFileNeedsMainProgram()
    {
        // A program file with definitions but no main expression reports the
        // missing main program like an empty program does (upstream #2785
        // pins the exit code; wording stays in our diagnostic shape).
        var host = new MockFileSystem();
        host.AddFile("/main.jq", "def a: .;\n");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-f", "/main.jq")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("Top-level program not given", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_ImportOnlyFileNeedsMainProgram()
    {
        // An import list with no main expression reports the missing main
        // program like an empty or definition-only program does.
        var host = new MockFileSystem();
        host.AddFile("/main.jq", "import \"a\" as y;\n");
        host.AddFile("/a.jq", "def a: 1;");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-f", "/main.jq")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("Top-level program not given", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_FileProgramWithMainRuns()
    {
        var host = new MockFileSystem();
        host.AddFile("/main.jq", "def a: .;\n0\n");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-f", "/main.jq")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_NestedDefWithoutBodyKeepsTokenError()
    {
        // The missing-main diagnostic fires only at top level: a bodiless
        // definition inside parentheses keeps its unexpected-token error.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "(def a: .;)")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("unexpected token", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("\"abc", "unterminated string")]
    [InlineData("`", "invalid character")]
    public async Task Jq_LexerErrorsAreCompileErrors(string filter, string diagnostic)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public void Jq_SourceSpanCountsLinesAndColumns()
    {
        JqSourceSpan first = JqSourceSpan.FromOffset("a\nbc", 0);
        JqSourceSpan second = JqSourceSpan.FromOffset("a\nbc", 2);
        JqSourceSpan third = JqSourceSpan.FromOffset("a\nbc", 3);

        Assert.Equal(new JqSourceSpan(0, 1, 1), first);
        Assert.Equal(new JqSourceSpan(2, 2, 1), second);
        Assert.Equal(new JqSourceSpan(3, 2, 2), third);
    }

    [Fact]
    public void Jq_RuntimeTypeErrorCarriesStructuredPayload()
    {
        var variables = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
        var budget = new JqBudget(CancellationToken.None);
        using var context = new JqContext(variables, JqProgramSource.Inline, budget);
        JqFilter filter = new JqParser("1 | .foo", JqProgramSource.Inline, context.RootEnvironment, budget).Parse();

        JqRuntimeException error = Assert.Throws<JqRuntimeException>(() => { filter.Evaluate(null, context, context.RootEnvironment).ToList(); });
        Assert.Contains("cannot index number", error.Message);
        Assert.Null(error.Payload);
    }

    private sealed class OversizedReadHost : IJqHost
    {
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(JqByteReadResult.Success(buffer.Length + 1, false));
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            return Task.FromResult(JqAppendResult.Open);
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken)
        {
            return Task.FromResult(JqOpenedFile.Failure("no files"));
        }

        public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }
}

