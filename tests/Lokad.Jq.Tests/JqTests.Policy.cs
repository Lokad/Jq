using System.Threading;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("\"x\"", "test(\"x\" * 16385)", "regex pattern exceeds")]
    [InlineData("\"x\"", "test(\"(\" * 65 + \"x\" + \")\" * 65)", "invalid regex")]
    [InlineData("\"a\" * 1000 + \"!\"", "test(\"(*NO_START_OPT)(*NO_AUTO_POSSESS)(a+)+$\")", "regex matching failed")]
    [InlineData("\"a\" * 6000", "test(\"(*NO_START_OPT)(*NO_AUTO_POSSESS)a.*b\")", "regex work limit exceeded")]
    public async Task Jq_RegexQuotasBypassLanguageHandlers(string input, string expression, string diagnostic)
    {
        foreach (string handler in new[] { "try (" + expression + ") catch \"caught\"", "(" + expression + ")?", "(" + expression + ")? // \"fallback\"" })
        {
            var host = new MockFileSystem();
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", input + " | " + handler)));

            Assert.Equal(5, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
            Assert.StartsWith("jq: ", host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
            Assert.Contains(diagnostic, host.GetOutput(JqFileDescriptor.StdErr), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Jq_RegexQuotaStopsLaterInputsAndClosesOwnedFiles()
    {
        var host = new MockFileSystem();
        host.AddFile("/first", "\"ok\"\n\"" + new string('a', 6000) + "\"\n\"ok\"\n");
        host.AddFile("/later", "\"ok\"\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq",
            "if . == \"ok\" then . else try test(\"(*NO_START_OPT)(*NO_AUTO_POSSESS)a.*b\") catch \"caught\" end",
            "/first", "/later")));

        Assert.Equal(5, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"ok\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: regex work limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_InvalidRegexSyntaxRemainsCatchable()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"x\" | try test(\"[\") catch \"caught\"")));

        Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Equal("\"caught\"\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("1\n", "", "1\n")]
    [InlineData("ab\n", "-R", "ab\n")]
    [InlineData("\u001e1\n", "--seq", "\u001e1\n")]
    [InlineData("[1]\n", "--stream", "[[0],1]\n[[0]]\n")]
    public async Task Jq_PolicyBoundsInputModesAtExactBytes(string input, string mode, string expected)
    {
        var invocation = mode.Length == 0
            ? BuildInvocation("jq", "-c", "-r", ".")
            : BuildInvocation("jq", "-c", "-r", mode, ".");
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        var policy = new JqExecutionPolicy { MaximumInputBytes = System.Text.Encoding.UTF8.GetByteCount(input) };
        var exact = new MockFileSystem();
        exact.SetStandardInput(input);

        Assert.Equal(0, await command.ExecuteAsync(exact, policy, CancellationToken.None));
        Assert.Equal(expected, exact.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(exact.GetOutput(JqFileDescriptor.StdErr));

        var over = new MockFileSystem();
        over.SetStandardInput(input + " ");
        Assert.Equal(5, await command.ExecuteAsync(over, policy, CancellationToken.None));
        Assert.Empty(over.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal($"jq: input exceeds the {policy.MaximumInputBytes}-byte limit\n", over.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_PolicyInputIsSharedAcrossOwnedFiles()
    {
        var host = new MockFileSystem();
        host.AddFile("/first", "1");
        host.AddFile("/second", "22");
        host.AddFile("/later", "3");
        var requests = new List<int>();
        host.BeforeByteRead = buffer => requests.Add(buffer.Length);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/first", "/second", "/later")));

        Assert.Equal(5, await command.ExecuteAsync(host, new JqExecutionPolicy { MaximumInputBytes = 2 }, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: input exceeds the 2-byte limit\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(new[] { 3, 2 }, requests);
        Assert.Equal(2, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("module")]
    [InlineData("rawfile")]
    [InlineData("slurpfile")]
    public async Task Jq_PolicyBoundsFilterModuleAndVariableFileReads(string kind)
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

        Assert.Equal(5, await command.ExecuteAsync(host, new JqExecutionPolicy { MaximumInputBytes = 1 }, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: input exceeds the 1-byte limit\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_PolicyOutputPreflightKeepsCompleteRecords()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-e", "1,2")));

        Assert.Equal(5, await command.ExecuteAsync(host, new JqExecutionPolicy { MaximumOutputBytes = 2 }, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: output exceeds the 2-byte limit\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(2, host.AppendCallCount); // One stdout record and one diagnostic.
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("--build-configuration")]
    public async Task Jq_PolicyBoundsInformationOutput(string option)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", option)));

        Assert.Equal(5, await command.ExecuteAsync(host, new JqExecutionPolicy { MaximumOutputBytes = 1 }, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: output exceeds the 1-byte limit\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_PolicyStopsOnDownstreamClosureBeforeLaterExhaustion()
    {
        var host = new MockFileSystem { AppendRemainsOpen = false };
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1,2")));

        Assert.Equal(0, await command.ExecuteAsync(host, new JqExecutionPolicy { MaximumOutputBytes = 2 }, CancellationToken.None));
        Assert.Equal("1\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(1, host.AppendCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_PolicyStagesSnapshotExhaustionBeforeAnyInput(bool nodes)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/missing")));
        var policy = nodes
            ? new JqExecutionPolicy { MaximumValueNodes = 1 }
            : new JqExecutionPolicy { MaximumAllocationBytes = 1 };

        Assert.Equal(5, await command.ExecuteAsync(host, policy, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(nodes ? "jq: value budget exceeded\n" : "jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, host.ReadBytesCallCount);
    }

    [Fact]
    public async Task Jq_PolicyCountsDiscardedEvaluation()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "try (range(100) | empty) catch \"caught\"")));

        Assert.Equal(5, await command.ExecuteAsync(host, new JqExecutionPolicy { MaximumValueNodes = 128 }, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: value budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Theory]
    [InlineData(32, "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n", 0)]
    [InlineData(33, "", 5)]
    public async Task Jq_PolicyStringLimitPreflightsExpansion(int count, string expected, int status)
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "-r", $"try (\"x\" * {count}) catch \"caught\"")));

        Assert.Equal(status, await command.ExecuteAsync(host, new JqExecutionPolicy { MaximumStringLength = 32 }, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(status == 0 ? "" : "jq: string result exceeds the 32 UTF-16 code unit limit\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public async Task Jq_PolicyRegexWorkCannotBeCaught()
    {
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"x\" | try test(\"x\") catch \"caught\"")));

        Assert.Equal(5, await command.ExecuteAsync(host, new JqExecutionPolicy { MaximumRegexWork = 1 }, CancellationToken.None));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: regex work limit exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
    }

    [Fact]
    public void Jq_PolicyRegexTimeUsesConfiguredAllowance()
    {
        var clock = new RegexTestClock();
        var policy = new JqExecutionPolicy { MaximumRegexTime = TimeSpan.FromTicks(1) };
        using var regexes = new JqRegexCache(new JqBudget(policy, CancellationToken.None), clock);
        var pattern = regexes.Get("x", PCRE.PcreOptions.None);
        Assert.True(regexes.Match(pattern, "x", 0, PCRE.PcreMatchOptions.None).Success);

        clock.ReadAdvance = 1;
        Assert.Equal("regex time limit exceeded", Assert.Throws<JqQuotaException>(() => regexes.Match(pattern, "x", 0, PCRE.PcreMatchOptions.None)).Message);
    }

    [Fact]
    public async Task Jq_PolicyCountersResetForSharedConcurrentCommands()
    {
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "range(4)")));
        var policy = new JqExecutionPolicy { MaximumOutputBytes = 8 };
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var host = new MockFileSystem();
                Assert.Equal(0, await command.ExecuteAsync(host, policy, CancellationToken.None));
                Assert.Equal("0\n1\n2\n3\n", host.GetOutput(JqFileDescriptor.StdOut));
                Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            }
        })));

        var strict = new MockFileSystem();
        Assert.Equal(5, await command.ExecuteAsync(strict, policy with { MaximumOutputBytes = 2 }, CancellationToken.None));
        var ordinary = new MockFileSystem();
        Assert.Equal(0, await command.ExecuteAsync(ordinary, CancellationToken.None));
        Assert.Equal("0\n1\n2\n3\n", ordinary.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(8, policy.MaximumOutputBytes);
    }

    [Fact]
    public async Task Jq_PolicyCancellationWinsBeforeSetupAndWrites()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "1")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteAsync(host,
            new JqExecutionPolicy { MaximumAllocationBytes = 1 }, cancellation.Token));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, host.AppendCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Jq_PolicyRejectsUntestedOrNonPositiveAllowances(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JqExecutionPolicy { MaximumInputBytes = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JqExecutionPolicy { MaximumOutputBytes = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JqExecutionPolicy { MaximumAllocationBytes = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JqExecutionPolicy { MaximumValueNodes = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JqExecutionPolicy { MaximumStringLength = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JqExecutionPolicy { MaximumRegexWork = value });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JqExecutionPolicy { MaximumRegexTime = TimeSpan.FromSeconds(value) });
    }
}
