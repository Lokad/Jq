using Lokad.Jq;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("[\"x\" * 4194304, \"y\" * 4194305] | join(\"\") | length")]
    [InlineData("[\"\", \"\", \"\"] | join(\"x\" * 4194305) | length")]
    [InlineData("\"x\" * 4194305 | \"\\(.)\\(.)\" | length")]
    [InlineData("\"<\" * 2097153 | @html | length")]
    [InlineData("\"/\" * 2796203 | @uri | length")]
    [InlineData("\"x\" * 6291457 | @base64 | length")]
    [InlineData("\"'\" * 2097152 | @sh | length")]
    [InlineData("[\"\\t\" * 4194305] | @tsv | length")]
    [InlineData("\"\\u0000\" * 1398102 | tojson | length")]
    [InlineData("\"xx\" | gsub(\"x\"; \"y\" * 4194305) | length")]
    public async Task Jq_RejectsStringBuilderAmplification(string filter)
    {
        // Even without limits, each result is only slightly above 8 Mi UTF-16 code units.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("string result exceeds", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("[\"x\" * 4194304, \"y\" * 4194304] | join(\"\") | length", "8388608\n")]
    [InlineData("\"x\" * 4194304 | \"\\(.)\\(.)\" | length", "8388608\n")]
    [InlineData("[\"é\", \"🚀\"] | join(\"/\")", "\"é/\\uD83D\\uDE80\"\n")]
    [InlineData("[range(2147483646;2147483647;2)]", "[\n  2147483646\n]\n")]
    [InlineData("[range(-2147483647;-2147483648;-2)]", "[\n  -2147483647\n]\n")]
    [InlineData("[10,20][range(0;1)]", "10\n")]
    [InlineData("\"a,,b,\" | split(\",\")", "[\n  \"a\",\n  \"\",\n  \"b\",\n  \"\"\n]\n")]
    [InlineData("\"ab\" | indices(\"\")", "[]\n")]
    [InlineData("\"x\" * 300000 | index(\"x\")", "0\n")]
    [InlineData("\"ab\" | index(\"\")", "null\n")]
    [InlineData("\"ab\" | index(\"z\")", "null\n")]
    [InlineData("[1,null,1] | [index(1), indices(1), index(null), index(2)]", "[\n  0,\n  [\n    0,\n    2\n  ],\n  1,\n  null\n]\n")]
    [InlineData("(\"\\u0000\" * 1400000) == (\"\\u0000\" * 1400000)", "true\n")]
    [InlineData("[null == null, null == false, 1 == \"1\", [1,2] == [1,2], [1,2] != [2,1]]", "[\n  true,\n  false,\n  false,\n  true,\n  true\n]\n")]
    [InlineData("[\"a,b\", \"c\\\"d\"] | @csv", "\"\\\"a,b\\\",\\\"c\\\"\\\"d\\\"\"\n")]
    [InlineData("\"é🚀\" | @base64 | @base64d", "\"é\\uD83D\\uDE80\"\n")]
    [InlineData("[\"\\t\" * 4194304] | @tsv | length", "8388608\n")]
    [InlineData("\"x\" * 6291456 | tojson | length", "6291458\n")]
    [InlineData("\"a🚀bc\"[1:2]", "\"\\uD83D\\uDE80\"\n")]
    [InlineData("\"a🚀bc\"[4:]", "\"\"\n")]
    [InlineData("\"a🚀bc\"[2:2]", "\"\"\n")]
    [InlineData("\"xx\" | gsub(\"x\"; \"y\" * 4194304) | length", "8388608\n")]
    public async Task Jq_MemoryLimitsPreserveOrdinaryResults(string filter, string expected)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("[range(0;300000)] | length")]
    [InlineData("\"x\" * 300000 | explode | length")]
    [InlineData("\"x\" * 300000 | indices(\"x\") | length")]
    [InlineData("\",\" * 300000 | split(\",\") | length")]
    [InlineData("{a:range(0;300000)} | length")]
    [InlineData("range(0;100000) | empty")]
    [InlineData("select(range(0;100000) | false)")]
    [InlineData("\"x\" * 300000 | gsub(\"x\"; \"\") | empty")]
    // Multi-output truthy conditions duplicate the state forever; the cumulative budget stages the hang with a record-complete prefix.
    [InlineData("5 | while((true,false); .)")]
    // Constant multi-output updates fan out forever the same way; the prefix still streams record-complete.
    [InlineData("0 | while(.<2; 1,2)")]
    // Upstream day-of-week/yearday loop: cumulative node charges (no refunds) trip the value budget; staged exit 5.
    [InlineData("last(range(365 * 67)|(\"1970-03-01T01:02:03Z\"|strptime(\"%Y-%m-%dT%H:%M:%SZ\")|mktime) + (86400 * .)|strftime(\"%Y-%m-%dT%H:%M:%SZ\")|strptime(\"%Y-%m-%dT%H:%M:%SZ\"))")]
    // Upstream large-offset slice vector: ranging down over a 65k sparse array rebuilds per update, tripping the value budget; staged exit 5.
    [InlineData("reduce range(65540;65536;-1) as $i ([]; .[$i] = $i)|.[65536:]")]
    public async Task Jq_BoundsValuesAndDiscardedWork(string filter)
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        // Lazy generators stream prefix results before the cumulative
        // budget trips; every emitted record stays complete. The eager
        // buffering that produced no output here was the bug, not the bound.
        string stdout = host.GetOutput(JqFileDescriptor.StdOut);
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(int.TryParse(line, out _), "Partial output must stay record-complete: " + line);
    }

    [Fact]
    public async Task Jq_ChargesStringsAcrossManySmallResults()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n",
            "[range(0;150) | \"x\" * 262144] | length")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("memory budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_BoundsJsonBeforeMaterializingInputOrArguments(bool argument)
    {
        var json = "[" + string.Join(',', Enumerable.Repeat("null", 262145)) + "]";
        var host = new MockFileSystem();
        host.SetStandardInput(json);
        var invocation = argument
            ? BuildInvocation("jq", "-n", "--argjson", "data", json, "$data | length")
            : BuildInvocation("jq", "length");
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(argument ? 2 : 5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_BoundsJsonargsBeforeMaterializingPositional()
    {
        // Like --argjson, quota trips while parsing positional JSON report
        // exit 2 with the budget message instead of a JSON syntax error.
        var json = "[" + string.Join(",", Enumerable.Repeat("null", 262145)) + "]";
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "$ARGS.positional[0] | length", "--jsonargs", json)));
        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_BoundsInputRequestsBeforeRejectingOversizeContent()
    {
        var host = new MockFileSystem();
        host.SetStandardInputBytes(new byte[JqBudget.MaximumInputBytes + 8192]);
        var requests = new List<int>();
        host.BeforeByteRead = buffer => requests.Add(buffer.Length);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("input exceeds", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(8192, requests.Max());
        Assert.Equal(2049, host.ReadBytesCallCount);
        Assert.Equal(JqBudget.MaximumInputBytes + 1, requests.Sum());
        // IJqHost exposes only byte reads.
    }

    [Fact]
    public async Task Jq_SharesInputLimitAcrossFilesAndClosesDescriptors()
    {
        var host = new MockFileSystem();
        host.AddFile("/a", "0" + new string(' ', 8 * 1024 * 1024 - 1));
        host.AddFile("/b", new string(' ', 8 * 1024 * 1024 + 1));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "empty", "/a", "/b")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("input exceeds", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.OpenFileCount);
        // IJqHost exposes only byte reads.
    }

    [Fact]
    public async Task Jq_InputCancellationClosesDescriptor()
    {
        var host = new MockFileSystem();
        host.AddFile("/data", "{}\n");
        using var cancellation = new CancellationTokenSource();
        host.BeforeByteRead = _ => cancellation.Cancel();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/data")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(host, cancellation.Token));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("range(0;100000)")]
    [InlineData("select(range(0;100000))")]
    [InlineData("\"a\" | gsub(\"a\"; \"x\", \"y\")")]
    public async Task Jq_EvaluationObservesCancellationBetweenResults(string filter)
    {
        using var cancellation = new CancellationTokenSource();
        using var context = new JqContext(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(), JqProgramSource.Inline, new JqBudget(cancellation.Token));
        using var values = new JqParser(filter, JqProgramSource.Inline, context.RootEnvironment, context.Budget).Parse().Evaluate(null, context, context.RootEnvironment).GetEnumerator();
        Assert.True(values.MoveNext());
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => values.MoveNext());
    }

    [Theory]
    [InlineData("{\"a\":1}\n{\"a\":2}", ".a", "1\n2\n")]
    [InlineData("null 1 true", ".", "null\n1\ntrue\n")]
    [InlineData(" \t\r\n1\n2 \t\r\n", ".", "1\n2\n")]
    [InlineData(" \t\r\n", ".", "")]
    public async Task Jq_ReadsIndependentJsonValues(string input, string filter, string expected)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(input);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_RawInputPreservesCarriageReturns()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("a\rb\r\nc\r");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-R", ".")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"a\\rb\\r\"\n\"c\\r\"\n", host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData("range(0;300000)")]
    [InlineData("select(range(0;300000))")]
    [InlineData("range(0;300000) | \"x\" | gsub(\"x\"; \"y\")")]
    public async Task Jq_StopsWhenOutputCloses(string filter)
    {
        var host = new MockFileSystem { AppendRemainsOpen = false };
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.AppendCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Jq_BoundsParserAndEvaluationNesting(bool parse)
    {
        var filter = parse
            ? new string('(', 65) + "null" + new string(')', 65)
            : string.Join(" | ", Enumerable.Repeat(".", 300));
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(parse ? 3 : 5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("filter nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_RejectsOversizedTokenCount()
    {
        var host = new MockFileSystem();
        var filter = string.Join(",", Enumerable.Repeat("1", 3000));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("4096-token limit", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_RejectsOversizedArgumentCount()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", Enumerable.Repeat(".", 4097).ToArray(), [])));
        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("argument count exceeds the 4096 limit", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_RejectsOversizedDefinitions()
    {
        // Mirrors the reference overflow guards: 4097 parameters and
        // 4097 sibling definitions both stop at the token policy.
        string parameters = string.Join(";", System.Linq.Enumerable.Range(0, 4097).Select(i => "a" + i));
        string arguments = string.Join(";", System.Linq.Enumerable.Range(0, 4097));
        foreach (string filter in new[]
        {
            "def f(" + parameters + "): .; f(" + arguments + ")",
            string.Join("; ", System.Linq.Enumerable.Range(0, 4097).Select(i => "def f" + i + ": " + i)) + "; " + string.Join(" + ", System.Linq.Enumerable.Range(0, 4097).Select(i => "f" + i)),
        })
        {
            var host = new MockFileSystem();
            var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
            Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
            Assert.Contains("4096-token limit", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_SlurpsAcrossFiles(bool raw)
    {
        var host = new MockFileSystem();
        host.AddFile("/a", "1\n");
        host.AddFile("/b", "2\n");
        var invocation = raw ? BuildInvocation("jq", "-R", "-s", ".", "/a", "/b")
            : BuildInvocation("jq", "-s", ".", "/a", "/b");
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(raw ? "\"1\\n2\\n\"\n" : "[\n  1,\n  2\n]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_BoundsSlurpAcrossManySmallInputs()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat("null\n", 262145)));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-s", "length")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_BoundsRawSlurpBeforeConcatenatingFiles()
    {
        var host = new MockFileSystem();
        host.AddFile("/a", new string('x', 4194304));
        host.AddFile("/b", new string('y', 4194305));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-R", "-s", "length", "/a", "/b")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("string result exceeds", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_BoundsRenderingBeforeWriting()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"\\u0000\" * 1398102")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("string result exceeds", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_BoundsCumulativeOutput()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", "range(0;17) | \"é\" * 1048576")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("output exceeds", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.True(host.GetOutputBytes(JqFileDescriptor.StdOut).Length < 32 * 1024 * 1024);
    }

    [Fact]
    public async Task Jq_BoundsFilterFileAndClosesDescriptor()
    {
        var host = new MockFileSystem();
        host.AddFile("/filter", new string(' ', 1024 * 1024 + 1));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-f", "/filter")));

        Assert.Equal(3, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("filter exceeds", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("uri")]
    public async Task Jq_EncodesSurrogatePairsAcrossChunks(string format)
    {
        var text = new string('x', 1023) + "🚀<";
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", "--arg", "text", text, $"$text | @{format}")));
        var expected = format == "html" ? text.Replace("<", "&lt;") : Uri.EscapeDataString(text);

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected + "\n", host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData(64, 0)]
    [InlineData(65, 5)]
    public async Task Jq_BoundsGeneratedJsonDepth(int depth, int expectedExit)
    {
        var host = new MockFileSystem();
        var filter = "null | " + string.Join(" | ", Enumerable.Repeat("[.]", depth));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(expectedExit, await tool.ExecuteAsync(host, CancellationToken.None));
        if (expectedExit != 0)
            Assert.Contains("value nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_JsonArgumentsStillRequireOneValue()
    {
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "--argjson", "x", "1 2", "$x")));

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("single JSON value", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("--argjson")]
    [InlineData("--jsonargs")]
    [InlineData("stdin")]
    [InlineData("fromjson")]
    public async Task Jq_DuplicateJsonPropertiesKeepLastValue(string source)
    {
        // The reference resolves duplicate keys last-wins at first position
        // (object assignment semantics); escapes decode before comparison.
        const string json = """{"outer":{"a":1,"\u0061":2},"b":1,"b":2}""";
        const string expected = "{\n  \"outer\": {\n    \"a\": 2\n  },\n  \"b\": 2\n}\n";
        var host = new MockFileSystem();
        host.SetStandardInput(json);
        var invocation = source switch
        {
            "--argjson" => BuildInvocation("jq", "-n", "--argjson", "data", json, "$data"),
            "--jsonargs" => BuildInvocation("jq", "-n", "$ARGS.positional[0]", "--jsonargs", json),
            "fromjson" => BuildInvocation("jq", "-R", "fromjson"),
            _ => BuildInvocation("jq", ".")
        };
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_InvalidUnicodePropertyReturnsDiagnostic(bool argument)
    {
        const string json = """{"\uD800":1}""";
        var host = new MockFileSystem();
        host.SetStandardInput(json);
        var invocation = argument
            ? BuildInvocation("jq", "-n", "--argjson", "data", json, "empty")
            : BuildInvocation("jq", "empty");
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(argument ? 2 : 5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("jq:", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_EqualityComparesParsedAndConstructedValues()
    {
        var host = new MockFileSystem();
        host.SetStandardInput("""{"first":[1,null,"é"],"second":true}""");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", """. == {second:true,first:[1,null,"é"]}""")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("true\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_StringLimitCountsDecodedJsonCharacters(bool property)
    {
        // The encoded token is over 8 Mi characters, but decodes to only 1.5 M characters.
        var token = "\"" + string.Concat(Enumerable.Repeat("\\u0061", 1500000)) + "\"";
        var host = new MockFileSystem();
        host.SetStandardInput(property ? "{" + token + ":0}" : token);
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "length")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(property ? "1\n" : "1500000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    public async Task Jq_DecodedJsonStringLimitHandlesMixedEscapes(int excess, int expectedExit)
    {
        var host = new MockFileSystem();
        // Six decoded UTF-16 units: ASCII, UTF-8, a simple escape, a backslash and a surrogate pair.
        host.SetStandardInput("\"" + new string('x', 8 * 1024 * 1024 - 6 + excess) + "\\u0061é\\n\\\\\\uD83D\\uDE80\"");
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "length")));

        Assert.Equal(expectedExit, await tool.ExecuteAsync(host, CancellationToken.None));
        if (expectedExit == 0)
        {
            Assert.Equal("8388607\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        }
        else
        {
            Assert.Contains("string result exceeds", host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        }
    }

    [Fact]
    public async Task Jq_RawInputAppliesStringLimitToEachLine()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(string.Concat(Enumerable.Repeat(new string('x', 1023) + "\n", 9 * 1024)));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-R", "length")));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal(string.Concat(Enumerable.Repeat("1023\n", 9 * 1024)), host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_RawInputStillRejectsOversizedLines()
    {
        var host = new MockFileSystem();
        host.SetStandardInput(new string('x', 8 * 1024 * 1024 + 1));
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-R", "empty")));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("string result exceeds", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_BoundsPathResolutionBeforeExpandingWorkingDirectory(bool filterFile)
    {
        // Keep probes bounded even without the guard: about 8 MiB of retained paths.
        var directory = "/" + new string('x', filterFile ? 4 * 1024 * 1024 : 65536);
        var args = filterFile
            ? new[] { "-n", "-f", "filter" }
            : new[] { "-n", "empty" }.Concat(Enumerable.Repeat("file", 64)).ToArray();
        var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", args, [new JqEnvironmentVariable("PWD", directory)]);
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(2, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("memory budget exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_PathBudgetPreservesPathResolution(bool absolute)
    {
        var host = new MockFileSystem();
        host.AddFile("/work/filter", ".n");
        host.AddFile("/work/data", """{"n":2}""");
        var directory = absolute ? "/" + new string('x', 4 * 1024 * 1024) : "/work/sub";
        var args = absolute ? new[] { "-f", "/work/filter", "/work/data" } : new[] { "-f", "../filter", ".././data" };
        var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", args, [new JqEnvironmentVariable("PWD", directory)]);
        var tool = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(0, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("2\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("reduce range(10000) as $_ ([];[.]) | tojson | try (fromjson) catch . | (contains(\"<skipped: too deep>\") | not) and contains(\"Exceeds depth limit for parsing\")")]
    [InlineData("reduce range(10001) as $_ ([]; [.]) | tojson | contains(\"<skipped: too deep>\")")]
    [InlineData("reduce range(10000) as $_ ([]; [.]) | contains([[]])")]
    [InlineData("try (reduce range(10001) as $_ ([]; [.]) as $x | $x | contains($x)) catch .")]
    [InlineData("try (reduce range(10001) as $_ ({}; {a: .}) as $x | $x * $x) catch .")]
    [InlineData("try ((reduce range(10001) as $_ ([]; [.])) as $x | (reduce range(10001) as $_ ([]; [.])) as $y | $x == $y) catch .")]
    [InlineData("try ((reduce range(10001) as $_ ([]; [.])) as $x | [$x, $x] | sort) catch .")]
    [InlineData("try ((reduce range(10001) as $_ ([]; [.])) as $x | [$x, $x] | unique) catch .")]
    [InlineData("try ((reduce range(10001) as $_ ({}; {a: .})) as $x | [$x, $x] | sort) catch .")]
    [InlineData("try ((reduce range(10001) as $_ ({}; {a: .})) as $x | [$x, $x] | unique) catch .")]
    [InlineData("reduce range(9999) as $_ ([];[.]) | tojson | fromjson | flatten")]
    [InlineData("reduce range(10000) as $_ ({}; {a: .}) as $x | $x * $x | length")]
    public async Task Jq_RejectsExcessiveValueNesting(string filter)
    {
        // Deeply nested construction trips the cumulative value policy before
        // any recursive builtin runs, so the reference deep-content checks
        // surface here as staged failures instead of caught jq errors.
        var host = new MockFileSystem();
        var tool = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));

        Assert.Equal(5, await tool.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("value nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }
}
