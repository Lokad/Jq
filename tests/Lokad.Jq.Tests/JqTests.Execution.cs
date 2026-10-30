using Lokad.Jq.Benchmarking;
using System.Threading;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public async Task Jq_InvalidRegexSyntaxRemainsCatchable()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"x\" | try test(\"[\") catch \"caught\"")));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("\"caught\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1\n", "", "1\n")]
    [InlineData("ab\n", "-R", "ab\n")]
    [InlineData("\u001e1\n", "--seq", "\u001e1\n")]
    [InlineData("[1]\n", "--stream", "[[0],1]\n[[0]]\n")]
    public async Task Jq_InputModesPreserveTrailingWhitespaceSemantics(string input, string mode, string expected)
    {
        var invocation = mode.Length == 0
            ? BuildInvocation("jq", "-c", "-r", ".")
            : BuildInvocation("jq", "-c", "-r", mode, ".");
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        var regexEngine = Utf8RegexAdapter.Instance;
        var exact = new MockFileSystem();
        exact.SetStandardInput(input);

        Assert.Equal(0, await command.ExecuteAsync(exact, regexEngine, CancellationToken.None));
        Assert.Equal(expected, exact.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(exact.GetOutput(JqFileDescriptor.StdErr));

        var over = new MockFileSystem();
        over.SetStandardInput(input + " ");
        Assert.Equal(0, await command.ExecuteAsync(over, regexEngine, CancellationToken.None));
        Assert.Equal(mode == "-R" ? expected + " \n" : expected, over.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(over.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_InputTraversesOwnedFilesWithBoundedRequests()
    {
        var host = new MockFileSystem();
        host.AddFile("/first", "1");
        host.AddFile("/second", "22");
        host.AddFile("/later", "3");
        var requests = new List<int>();
        host.BeforeByteRead = buffer => requests.Add(buffer.Length);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/first", "/second", "/later")));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("1223\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(new[] { 8192, 8192, 8192 }, requests);
        Assert.Equal(3, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("module")]
    [InlineData("rawfile")]
    [InlineData("slurpfile")]
    public async Task Jq_HostedReadsCloseOwnedFilesAfterCompletion(string kind)
    {
        var host = new MockFileSystem();
        host.AddFile("/data", "12");
        host.AddFile("/program", "12");
        host.AddFile("/module.jq", "def f: 12;");
        string[] arguments = kind switch
        {
            "filter" => ["-n", "-f", "/program"],
            "module" => ["-n", "-L", "/", "import \"module\" as m; m::f"],
            "rawfile" => ["-n", "--rawfile", "data", "/data", "$data"],
            _ => ["-n", "--slurpfile", "data", "/data", "$data"],
        };
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, [])));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        string expected = kind switch
        {
            "rawfile" => "\"12\"\n",
            "slurpfile" => "[\n  12\n]\n",
            _ => "12\n",
        };
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_DownstreamClosureStopsLaterOutputs()
    {
        var host = new MockFileSystem { AppendRemainsOpen = false };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1,2")));

        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.AppendCallCount);
    }

    [Fact]
    public async Task Jq_DiscardedEvaluationUsesBoundedSharedAdmission()
    {
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "range(100000) | empty")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.PeakBytes, 1, 64 * 1024);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_RegexUnadmittedCalloutRemainsCatchable()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"x\" | try test(\"(?C1)x\") catch \"caught\"")));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Equal("\"caught\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_RegexCancellationPropagatesAtSearchBoundary(bool detailed)
    {
        using var cancellation = new CancellationTokenSource();
        using var regexes = new JqRegexCache(new JqBudget(Utf8RegexAdapter.Instance, cancellation.Token));
        using var patternLease = regexes.Rent("x"u8.ToArray(), JqRegexCompileOptions.None);
        var pattern = patternLease.Value;
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() =>
        {
            if (detailed) regexes.Match(pattern, "x"u8.ToArray(), 0, JqRegexMatchOptions.None);
            else regexes.IsMatch(pattern, "x"u8.ToArray(), JqRegexMatchOptions.None);
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Jq_CancellationWinsBeforeSetupAndWrites()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host,
            Utf8RegexAdapter.Instance, cancellation.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.AppendCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_DataImportQuotasKeepTheirTerminalCategory(bool optional)
    {
        var host = new MockFileSystem();
        host.AddFile("/data.json", "[" + string.Join(',', Enumerable.Repeat("0", 1000)) + "]");
        string metadata = optional ? " {\"optional\":true}" : "";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-L", "/",
            "import \"data\" as $data" + metadata + "; empty")));

        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, new BenchmarkMemoryBudget(32 * 1024), CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_InputQuotaAbandonsWithoutRetryingDecode()
    {
        var host = new MockFileSystem();
        host.AddFile("/data", "[" + string.Join(',', Enumerable.Repeat("0", 200)) + "]" + new string(' ', 9000));
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/data")));

        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, new BenchmarkMemoryBudget(32 * 1024), CancellationToken.None));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.ReadBytesCallCount);
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_StreamNumericDecodePreservesAdmissionRefusal(bool finish)
    {
        var memory = new BenchmarkMemoryBudget(256);
        using var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None);
        using var scanner = new JqStreamScanner(budget, new JqRuntime(budget), false, false);
        scanner.Consume("1"u8);
        var failure = Assert.Throws<JqQuotaException>(() =>
        {
            if (finish) scanner.FinishFinal();
            else scanner.Consume(" "u8);
        });
        Assert.True(failure.IsMemoryAdmission);
        Assert.Equal("memory budget exceeded", failure.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--seq")]
    [InlineData("--stream")]
    [InlineData("--stream-errors")]
    public async Task Jq_InputDepthQuotaBypassesExplicitInputHandlers(string mode)
    {
        var host = new MockFileSystem();
        host.SetStandardInput((mode == "--seq" ? "\u001e" : "") + new string('[', 65) + "0" + new string(']', 65));
        var invocation = mode.Length == 0
            ? BuildInvocation("jq", "-n", "try inputs catch \"caught\"")
            : BuildInvocation("jq", "-n", mode, "try inputs catch \"caught\"");
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));

        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: value nesting limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Jq_AdmissionRejectsLargeJsonStringsBeforeDecoding(bool objectKey, bool escaped)
    {
        string token = "\"" + (escaped ? string.Concat(Enumerable.Repeat("\\u0061", 262144)) : new string('a', 262144)) + "\"";
        byte[] input = System.Text.Encoding.UTF8.GetBytes(objectKey ? "{" + token + ":0}" : token);
        var memory = new BenchmarkMemoryBudget(256);
        Assert.Throws<JqQuotaException>(() => new JqRuntime(new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None)).ReadJsonValue(input, out _));

        var runtime = new JqRuntime(new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        JqQuotaException? failure = null;
        long started = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            runtime.ReadJsonValue(input, out _);
        }
        catch (JqQuotaException quota)
        {
            failure = quota;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - started;

        Assert.Equal("memory budget exceeded",
            Assert.IsType<JqQuotaException>(failure).Message);
        Assert.True(allocated < 65536, $"Rejected JSON must not allocate its decoded string; allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData("\"éx\"", "éx", false)]
    [InlineData("\"éx\"", "éx", true)]
    [InlineData("\"😀\"", "😀", false)]
    [InlineData("\"😀\"", "😀", true)]
    [InlineData("\"\\uD83D\\uDE00\"", "😀", false)]
    [InlineData("\"\\uD83D\\uDE00\"", "😀", true)]
    [InlineData("\"\\n\\t\"", "\n\t", false)]
    [InlineData("\"\\n\\t\"", "\n\t", true)]
    [InlineData("\"\\\\u0061\"", "\\u0061", false)]
    [InlineData("\"\\\\u0061\"", "\\u0061", true)]
    public void Jq_JsonStringPreflightCountsDecodedCodeUnitsOnce(string token, string expected, bool objectKey)
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes(objectKey ? "{" + token + ":0}" : token);
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None);
        var runtime = new JqRuntime(budget);

        var value = runtime.ReadJsonValue(input, out int consumed);
        Assert.Equal(input.Length, consumed);
        if (objectKey)
            Assert.Equal(expected, Assert.Single(Assert.IsType<JqObject>(value)).Key.GetValue<string>());
        else
            Assert.Equal(expected, Assert.IsAssignableFrom<JqScalar>(value).GetValue<string>());

        Assert.InRange(memory.ReservedBytes, 2L * expected.Length, 1024);
        budget.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }
}
