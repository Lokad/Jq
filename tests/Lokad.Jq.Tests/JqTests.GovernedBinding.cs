using Lokad.Jq.Benchmarking;
namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("--arg", "\"1\"")]
    [InlineData("--argjson", "1")]
    [InlineData("--rawfile", "\"one\"")]
    [InlineData("--slurpfile", "[1]")]
    public async Task Jq_FirstBindingWinsAcrossAllArgumentKinds(string first, string expected)
    {
        foreach (string duplicate in new[] { "--arg", "--argjson", "--rawfile", "--slurpfile" })
        {
            var memory = new BenchmarkMemoryBudget(128 * 1024);
            var host = new MockFileSystem();
            host.AddFile("/data", first == "--rawfile" ? "one" : "1");
            bool file = first is "--rawfile" or "--slurpfile";
            var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", first, "v",
                file ? "/data" : "1", duplicate, "v", duplicate == "--argjson" ? "not-json" : "/missing",
                "[$v, $ARGS.named.v]")));

            Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
            Assert.Equal("[" + expected + "," + expected + "]\n", host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Equal(file ? 1 : 0, host.ReadBytesCallCount);
            Assert.Equal(file ? 1 : 0, host.ClosedDescriptors.Count);
            Assert.Equal(0, host.OpenFileCount);
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Fact]
    public void Jq_ArgumentBindingPreservesFiveThousandOrderedFileNames()
    {
        var arguments = new List<string> { "-n", "." };
        for (int index = 0; index < 5000; index++)
            arguments.AddRange(["--rawfile", "v" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), "/data"]);
        var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, memory, CancellationToken.None))
        {
            var invocation = JqCommandLineParser.Parse(arguments, JqPath.Root, JqFileDescriptor.StdIn,
                JqFileDescriptor.StdOut, JqFileDescriptor.StdErr, [], null, budget);
            Assert.Null(invocation.Error);
            Assert.Equal(5000, invocation.FileVariables.Count);
            Assert.Equal(Enumerable.Range(0, 5000).Select(index => "v" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                invocation.FileVariables.Select(binding => binding.Name));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_BoundJsonAndArgsNamedFitWithoutAnotherExecutionCopy()
    {
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        string json = "[" + string.Join(',', Enumerable.Repeat("1", 1000)) + "]";
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "--argjson", "data", json, "[($data | length), ($ARGS.named.data | length)]")));
        var host = new MockFileSystem();

        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("[1000,1000]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_BoundVariablesAndFileArgumentsRemainPrivateAcrossCommandReuse()
    {
        var memory = new BenchmarkMemoryBudget(256 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "--argjson", "data", "[1,2]", "--rawfile", "note", "/note",
            "[($data | .[0] = 9), $data, $ARGS.named.data, $note, $ARGS.named.note]")));

        for (int round = 0; round < 2; round++)
        {
            var hosts = new[] { new MockFileSystem(), new MockFileSystem() };
            hosts[0].AddFile("/note", "first");
            hosts[1].AddFile("/note", "second");
            var delayed = hosts.Select(host => new MemoryBorrowHost(host)).ToArray();
            Task<int>[] executions = delayed.Select(host =>
                command.ExecuteAsync(host, memory, CancellationToken.None)).ToArray();
            try
            {
                await Task.WhenAll(delayed.Select(host => host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10))));
                Assert.All(executions, execution => Assert.False(execution.IsCompleted));
            }
            finally
            {
                foreach (var host in delayed) host.Release.TrySetResult();
                await Task.WhenAll(executions);
            }
            Assert.All(executions, execution => Assert.Equal(0, execution.Result));
            for (int index = 0; index < hosts.Length; index++)
            {
                string note = index == 0 ? "first" : "second";
                Assert.Equal("[[9,2],[1,2],[1,2],\"" + note + "\",\"" + note + "\"]\n",
                    hosts[index].GetOutput(JqFileDescriptor.StdOut));
                Assert.Empty(hosts[index].GetOutput(JqFileDescriptor.StdErr));
                Assert.Single(hosts[index].ClosedDescriptors);
                Assert.Equal(0, hosts[index].OpenFileCount);
            }
            Assert.Equal(0, memory.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("--rawfile")]
    [InlineData("--slurpfile")]
    public async Task Jq_GovernedFileVariableAllocationFailurePropagatesAndRefunds(string option)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/data", "1");
        var failure = new OutOfMemoryException("simulated allocation failure");
        host.BeforeByteRead = _ => throw failure;
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", option, "data", "/data", "$data")));

        var actual = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.True(memory.PeakBytes > 0);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedInvocationFailureRefundsUnusedSnapshotAdmission()
    {
        var memory = new BenchmarkMemoryBudget(1024);
        Assert.Throws<OutOfMemoryException>(() => JqCommandInvocation.TryCreateWithStandardDescriptors(
            "jq", new FailingSnapshotArguments(), [], memory, out _));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.True(memory.PeakBytes > 0);
        Assert.True(JqCommandInvocation.TryCreateWithStandardDescriptors("jq", ["."], [],
            memory, out JqCommandInvocation? invocation));
        invocation.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_GovernedInvocationFactoriesPreserveExplicitClock(bool explicitDescriptors)
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var clock = new JqClock(new FixedClock(FixedInstant), TimeZoneInfo.Utc);
        JqCommandInvocation? invocation;
        bool admitted = explicitDescriptors
            ? JqCommandInvocation.TryCreate("jq", ["-n", "now"], [], JqPath.Root,
                JqFileDescriptor.StdIn, JqFileDescriptor.StdOut, JqFileDescriptor.StdErr, clock, memory, out invocation)
            : JqCommandInvocation.TryCreateWithStandardDescriptors("jq", ["-n", "now"], [], clock, memory, out invocation);
        Assert.True(admitted);
        Assert.NotNull(invocation);
        using (invocation)
        {
            Assert.Same(clock, invocation.Clock);
            var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
            var host = new MockFileSystem();
            Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.Equal(FixedInstant.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n",
                host.GetOutput(JqFileDescriptor.StdOut));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_GovernedBindingRefusalKeepsTerminalCategoryForJsonArguments(bool positional)
    {
        var memory = new BenchmarkMemoryBudget(32 * 1024);
        string json = "[" + string.Join(',', Enumerable.Repeat("0", 10000)) + "]";
        string[] arguments = positional
            ? ["-n", "$ARGS.positional[0] | length", "--jsonargs", json]
            : ["-n", "--argjson", "data", json, "$data | length"];
        var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, []);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(0, memory.ReservedBytes);
        var host = new MockFileSystem();

        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_GovernedInvocationRefusalPreservesCallerAdmission()
    {
        var memory = new BenchmarkMemoryBudget(64);
        using var caller = memory.CreateScope();
        Assert.True(caller.TryReserve(8, out IMemoryReservation? retained));
        using (retained)
        {
            Assert.False(JqCommandInvocation.TryCreateWithStandardDescriptors("jq",
                ["."], [], caller, out JqCommandInvocation? invocation));
            Assert.Null(invocation);
            Assert.Equal(8, memory.ReservedBytes);
            Assert.Equal(8, retained.Bytes);
            Assert.True(caller.TryReserve(56, out IMemoryReservation? available));
            available.Dispose();
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedInvocationSnapshotsStayChargedAcrossReuseAndBorrowTheirRoot()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        string[] arguments = ["-n", "-r", "--arg", "name", "original", "$name"];
        JqEnvironmentVariable[] environment = [new("PWD", "/work")];
        Assert.True(JqCommandInvocation.TryCreateWithStandardDescriptors("jq", arguments,
            environment, memory, out JqCommandInvocation? invocation));
        using (invocation)
        {
            long retained = memory.ReservedBytes;
            Assert.True(retained > 0);
            arguments[4] = "changed";
            environment[0] = new JqEnvironmentVariable("PWD", "/different");
            Assert.Equal("/work", invocation.CurrentDirectory.Path);
            var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
            Assert.Equal(retained, memory.ReservedBytes);
            for (int repeat = 0; repeat < 3; repeat++)
            {
                var host = new MockFileSystem();
                Assert.Equal(0, await command.ExecuteAsync(host, CancellationToken.None));
                Assert.Equal("original\n", host.GetOutput(JqFileDescriptor.StdOut));
                Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
                Assert.Equal(retained, memory.ReservedBytes);
            }
            invocation.Dispose();
            Assert.Throws<ObjectDisposedException>(() => invocation.Arguments);
            Assert.Throws<ObjectDisposedException>(() => invocation.Environment);
            Assert.Throws<ObjectDisposedException>(() => invocation.CurrentDirectory);
            Assert.Throws<ObjectDisposedException>(() => Jq.TryParse(invocation));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => command.ExecuteAsync(new MockFileSystem(), CancellationToken.None));
            Assert.Equal(0, memory.ReservedBytes);
            Assert.True(memory.TryReserve(128 * 1024, out IMemoryReservation? reused));
            reused.Dispose();
        }
    }

    [Fact]
    public async Task Jq_GovernedInvocationUsesExplicitDescriptorsAndPreservesArgumentErrors()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        var output = new JqFileDescriptor(10);
        var error = new JqFileDescriptor(11);
        Assert.True(JqCommandInvocation.TryCreate("jq", ["--argjson", "data", "invalid", "$data"],
            [], JqPath.Root, JqFileDescriptor.StdIn, output, error, memory, out JqCommandInvocation? invocation));
        using (invocation)
        {
            long retained = memory.ReservedBytes;
            var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
            var host = new MockFileSystem();
            Assert.Equal(2, await command.ExecuteAsync(host, CancellationToken.None));
            Assert.StartsWith("jq: invalid JSON for --argjson data:", host.GetOutput(error), StringComparison.Ordinal);
            Assert.Empty(host.GetOutput(output));
            Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
            Assert.Empty(host.ClosedDescriptors);
            Assert.Equal(retained, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_GovernedInvocationAndBindingStayLiveThroughOverlappingWritesAndCleanup()
    {
        var memory = new BenchmarkMemoryBudget(128 * 1024);
        Assert.True(JqCommandInvocation.TryCreateWithStandardDescriptors("jq", [".", "/input"],
            [], memory, out JqCommandInvocation? invocation));
        using (invocation)
        {
            long retained = memory.ReservedBytes;
            var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
            var firstInner = new MockFileSystem();
            var secondInner = new MockFileSystem();
            firstInner.AddFile("/input", "1\n");
            secondInner.AddFile("/input", "2\n");
            var first = new MemoryBorrowHost(firstInner) { DelayCleanup = true };
            var second = new MemoryBorrowHost(secondInner) { DelayCleanup = true };
            Task<int> left = command.ExecuteAsync(first, CancellationToken.None);
            await first.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long firstDemand = memory.ReservedBytes;
            Task<int> right = command.ExecuteAsync(second, CancellationToken.None);
            await second.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(memory.ReservedBytes > firstDemand);
            first.Release.SetResult();
            second.Release.SetResult();
            await Task.WhenAll(first.Closing.Task, second.Closing.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(memory.ReservedBytes > retained);
            first.CloseRelease.SetResult();
            second.CloseRelease.SetResult();
            Assert.Equal(new[] { 0, 0 }, await Task.WhenAll(left, right));
            Assert.Equal("1\n", firstInner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal("2\n", secondInner.GetOutput(JqFileDescriptor.StdOut));
            Assert.Equal(retained, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
    }

    private sealed class FailingSnapshotArguments : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => throw new OutOfMemoryException("Simulated snapshot failure.");
        public IEnumerator<string> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
