using Lokad.Jq.Benchmarking;
namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("object")]
    [InlineData("interpolation")]
    [InlineData("definitions")]
    [InlineData("metadata")]
    public async Task Jq_CompilesAdmittedShallowPrograms(string shape)
    {
        const int count = 5000;
        string filter = shape switch
        {
            "object" => "{" + string.Join(',', Enumerable.Range(0, count).Select(index => "k" + index + ":0")) + "} | length",
            "interpolation" => "\"" + string.Concat(Enumerable.Repeat("\\(0)", count)) + "\" | length",
            "definitions" => string.Concat(Enumerable.Range(0, count).Select(index => "def f" + index + ":" + index + ";")) + "f4999 + 1",
            _ => "module {items:[" + string.Join(',', Enumerable.Repeat("0", count)) + "]}; 5000",
        };
        var memory = new BenchmarkMemoryBudget(32 * 1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("5000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_BindsAdmittedArgumentVectors()
    {
        string[] arguments = ["-n", "--args", "$ARGS.positional | length", .. Enumerable.Repeat("value", 5000)];
        var memory = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, [])));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("5000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ReferenceFunctionArityRemainsACompileError()
    {
        string filter = "def f(" + string.Join(';', Enumerable.Range(0, 4097).Select(index => "a" + index)) + "): .; 0";
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Contains("function has too many parameters", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_TailRewriteGuardsItsActualRecursiveDepth()
    {
        string filter = "def f: " + string.Concat(Enumerable.Range(0, 300).Select(index => "def g" + index + ":0;")) + "f; 0";
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, CancellationToken.None));
        Assert.Contains("filter nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_LargeCompilationUsesSharedAdmissionAndCleansOwnedFiles(bool hosted)
    {
        string source = "{" + string.Join(',', Enumerable.Range(0, 5000).Select(index => "k" + index + ":0")) + "}";
        var memory = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/program", source);
        var invocation = hosted ? BuildInvocation("jq", "-n", "-f", "/program") : BuildInvocation("jq", "-n", source);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(5, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, memory, CancellationToken.None));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(hosted ? 1 : 0, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_CompilerSeedingStopsBeforeUnadmittedOrCanceledCopies(bool cancel)
    {
        var env = JqEnvironment.CreateRoot(new Dictionary<string, JqValue?>());
        for (int i = 0; i < 100000; i++)
            env = env.ExtendFunction(new JqFunctionDefinition("f" + i, [], new IdentityFilter()));
        using var compiler = new JqBudget(Utf8RegexAdapter.Instance, CancellationToken.None);
        var tokens = Lexer.Tokenize(".", JqProgramSource.Inline, compiler);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        var root = new BenchmarkMemoryBudget(0);
        var memory = new CountingMemoryBudget(root);
        using (var budget = new JqBudget(Utf8RegexAdapter.Instance, memory, cancellation.Token))
        {
            var parser = new JqParser(tokens, JqProgramSource.Inline, env, budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (cancel) Assert.ThrowsAny<OperationCanceledException>(() => parser.ParseBody(0));
            else Assert.True(Assert.Throws<JqQuotaException>(() => parser.ParseBody(0)).IsMemoryAdmission);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32768);
        }
        Assert.Equal(cancel ? 0 : 1, memory.Counts.Refusals);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_CompilerSeedsNearestFunctionsWithAmortizedAdmission()
    {
        var env = JqEnvironment.CreateRoot(new Dictionary<string, JqValue?>());
        for (int i = 0; i < 1000; i++)
            env = env.ExtendFunction(new JqFunctionDefinition("f" + i, [], new IdentityFilter()));
        env = env.ExtendFunction(new JqFunctionDefinition("f0", [new JqFunctionParameter("x", true)], new VariableFilter("x")));
        env = env.ExtendFunction(new JqFunctionDefinition("f0", [], new LiteralFilter(NativeFixtures.Create(42))));
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(),
            new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None)))
        {
            var filter = new JqParser("f0 + f0(1)", JqProgramSource.Inline, env, context.Budget).Parse();
            Assert.Equal(43, JqRuntime.Number(await filter.Evaluate(null, context, env).SingleAsync()));
            Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 100);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InterpolationBorrowsWideVisibleScopes()
    {
        var env = JqEnvironment.CreateRoot(new Dictionary<string, JqValue?>());
        for (int i = 0; i < 10000; i++)
            env = env.ExtendFunction(new JqFunctionDefinition("f" + i, [], new IdentityFilter()));
        var root = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        using (var context = new JqContext(new Dictionary<string, JqValue?>(),
            new JqBudget(Utf8RegexAdapter.Instance, memory, CancellationToken.None)))
        {
            string source = "\"" + string.Concat(Enumerable.Repeat("\\(f0)", 20)) + "\"";
            var filter = new JqParser(source, JqProgramSource.Inline, env, context.Budget).Parse();
            Assert.Equal(new string('1', 20), context.Runtime.ToNativeString(await filter.Evaluate(NativeFixtures.Create(1), context, env).SingleAsync()).GetValue<string>());
            Assert.True(root.PeakBytes < 2 * 1024 * 1024);
        }
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, memory.Counts.Refusals);
    }

    [Theory]
    [InlineData("def f:1; \"\\(def f:2; f)-\\(f)\"", "\"2-1\"\n")]
    [InlineData("def f:1; \"\\(def g:2; g)\", f", "\"2\"\n1\n")]
    [InlineData("label $out | \"\\(def f: break $out; f)\",42", "")]
    public async Task Jq_InterpolationKeepsPrivateDeclarationsAndLexicalParents(string source, string expected)
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_InterpolationDefinitionsStayPrivateAcrossFragments()
    {
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", "\"\\(def g:2; g)-\\(g)\"")));
        Assert.Equal(3, await command.ExecuteAsync(host, Utf8RegexAdapter.Instance, root, CancellationToken.None));
        Assert.Contains("unsupported function g", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, root.ReservedBytes);
    }

}
