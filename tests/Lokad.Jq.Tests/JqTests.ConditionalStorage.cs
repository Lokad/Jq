using System.Globalization;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("if", 1000)]
    [InlineData("if", 100000)]
    [InlineData("select", 1000)]
    [InlineData("select", 100000)]
    public async Task Jq_CompletedSingleValueConditionsKeepOneRecordBounded(string kind, int count)
    {
        const string condition = "(\"x\" * 128) == (\"x\" * 128)";
        string expression = kind == "if" ? "if " + condition + " then . else empty end" : "select(" + condition + ")";
        string filter = "\"ready\", (range(" + count.ToString(CultureInfo.InvariantCulture) + ") | " + expression + " | empty)";
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var counted = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nr", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, counted, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(counted.Counts.Reservations + counted.Counts.Resizes, 1, 40);
        Assert.Equal(0, counted.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("if (\"x\"*128)==(\"x\"*128) then \"r\"*128 else empty end")]
    [InlineData("select((\"x\"*128)==(\"x\"*128)) | \"r\"*128")]
    public async Task Jq_ConditionReconciliationKeepsCollectedResultsCharged(string expression)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nr", "\"ready\", (try ([range(100000) | " + expression + "] | length) catch \"caught\"), \"later\""], []);
        Assert.Equal(5, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("if \"x\"*1000000 then \"then\" else \"else\" end")]
    [InlineData("select(\"x\"*1000000)")]
    public async Task Jq_SingleValueConditionRefusalPrecedesBranchEffects(string expression)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq",
            ["-nr", "\"ready\", (try (" + expression + ") catch \"caught\"), \"later\""], []);
        Assert.Equal(5, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("ready\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ConditionReconciliationPreservesBorrowedInputAdmission(bool select)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(null, root, CancellationToken.None))
        {
            budget.BeginRecord();
            JqString input = JqString.FromHost(new string('p', 8192), budget);
            using var context = new JqContext(new Dictionary<string, JqValue?> { ["saved"] = input }, budget);
            var condition = new VariableFilter("saved");
            JqFilter filter = select ? new FunctionFilter("select", [condition])
                : new IfFilter([(condition, new IdentityFilter())], new LiteralFilter(null));
            var storage = Assert.IsType<JqStorage>(budget.TemporaryStorage);
            long before = storage.UsedBytes;
            Assert.True(before >= 8192);
            Assert.Same(input, await filter.Evaluate(input, context, context.RootEnvironment).SingleAsync());
            Assert.Equal(before, storage.UsedBytes);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_ConditionAndBranchErrorsRetainEscapingPayloads(bool conditionFails)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        using (var budget = new JqBudget(null, root, CancellationToken.None))
        using (var context = new JqContext(new Dictionary<string, JqValue?>(), budget))
        {
            JqFilter filter = conditionFails
                ? new IfFilter([(new FaultingSinglePredicate(), new IdentityFilter())], new LiteralFilter(null))
                : new JqParser("if (\"x\"*128)==(\"x\"*128) then error(\"e\"*8192) else 0 end",
                    JqProgramSource.Inline, context.RootEnvironment, budget).Parse();
            long compilation = root.ReservedBytes;
            budget.BeginRecord();
            var error = await Assert.ThrowsAsync<JqErrorException>(async () =>
            {
                _ = await filter.Evaluate(null, context, context.RootEnvironment).ToArrayAsync();
            });
            Assert.Equal(new string('e', 8192), error.Payload?.GetValue<string>());
            Assert.True(Assert.IsType<JqStorage>(budget.TemporaryStorage).UsedBytes >= 8192);
            Assert.True(root.ReservedBytes >= compilation + 8192);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("{a:1,b:2} | path(if (\"x\"*128)==(\"x\"*128) then .a else .b end)", "[\"a\"]\n")]
    [InlineData("{a:1} | path(select((\"x\"*128)==(\"x\"*128)) | .a)", "[\"a\"]\n")]
    [InlineData("if (true,false) then 1 else 2 end", "1\n2\n")]
    [InlineData("1 | select((true,false,true))", "1\n1\n")]
    [InlineData("if false then error(\"unused\") elif true then 1 else error(\"unused\") end", "1\n")]
    public async Task Jq_ConditionReconciliationPreservesPathsMultiplicityAndLazyBranches(string filter, string expected)
    {
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }

    private sealed class FaultingSinglePredicate : SingleValueFilter
    {
        protected override JqValue? EvaluateValue(JqValue? input, JqContext context, JqEnvironment environment) =>
            throw new JqErrorException(JqString.FromHost(new string('e', 8192), context.Budget));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_PureConditionalFallthroughUsesBoundedStack(bool paths)
    {
        string chain = "if false then .a " + string.Concat(Enumerable.Repeat("elif false then .a ", 1000)) + "else .b end";
        string filter = "{a:1,b:2} | " + (paths ? "path(" + chain + ")" : chain);
        var root = new BenchmarkMemoryBudget(16 * 1024 * 1024);
        var host = new MockFileSystem();
        using var invocation = JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-nc", filter], []);
        Assert.Equal(0, await Assert.IsType<Jq>(Jq.TryParse(invocation)).ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(paths ? "[\"b\"]\n" : "2\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
    }
}
