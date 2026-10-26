using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(64, 8)]
    [InlineData(4096, 1)]
    [InlineData(4096, 8)]
    public void Jq_QualifiedLookupBorrowsNamesAndPreservesDefinitionCapture(int aliasLength, int depth)
    {
        var root = JqEnvironment.CreateRoot(new Dictionary<string, JsonNode?>());
        var definition = new JqFunctionDefinition("f", [], new IdentityFilter());
        var capture = root.Extend("x", JsonValue.Create(42)).ExtendFunction(definition);
        JqEnvironment environment = capture;
        string alias = new('m', aliasLength);
        for (int i = 0; i < depth; i++) environment = root.ExtendModule(alias, environment);
        string qualified = string.Join("::", Enumerable.Repeat(alias, depth)) + "::f";
        JqUserClosure? closure = null;
        for (int i = 0; i < 100; i++) environment.TryGetFunctionQualified(qualified, 0, out closure);
        Assert.NotNull(closure);
        JqUserClosure first = closure;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            if (!environment.TryGetFunctionQualified(qualified, 0, out closure) || !ReferenceEquals(first, closure))
                throw new InvalidOperationException("Expected the qualified definition.");
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256);
        Assert.NotNull(closure);
        Assert.Same(definition, closure.Definition);
        Assert.Same(capture, closure.Environment);
        Assert.False(environment.TryGetFunctionQualified(qualified, 1, out _));
        Assert.False(environment.TryGetFunctionQualified(qualified.ToUpperInvariant(), 0, out _));
        Assert.False(environment.TryGetFunctionQualified(alias + "::", 0, out _));
        Assert.False(environment.TryGetFunctionQualified("::f", 0, out _));
    }

    [Fact]
    public void Jq_DefinitionClosuresRetainActivationIdentityAcrossRecursionAndExports()
    {
        var root = JqEnvironment.CreateRoot(new Dictionary<string, JsonNode?>());
        var definition = new JqFunctionDefinition("f", [], new VariableFilter("x"));
        var first = root.Extend("x", JsonValue.Create(1)).ExtendFunction(definition);
        var second = root.Extend("x", JsonValue.Create(2)).ExtendFunction(definition);
        Assert.True(first.TryGetFunctionQualified("f", 0, out var older));
        Assert.True(second.TryGetFunctionQualified("f", 0, out var newer));
        Assert.NotNull(older);
        Assert.NotNull(newer);
        Assert.NotSame(older, newer);
        Assert.Same(first, older.Environment);
        Assert.Same(second, newer.Environment);
        Assert.True(older.Environment.TryGetFunctionQualified("f", 0, out var recursive));
        Assert.Same(older, recursive);
        var alias = root.ExtendClosure("g", older);
        Assert.True(alias.TryGetFunctionQualified("g", 0, out var imported));
        Assert.Same(older, imported);
        using var budget = new JqBudget(JqUtf8RegexEngine.Instance, null, CancellationToken.None);
        var exports = new Dictionary<(string Name, int Arity), JqUserClosure>();
        second.CollectFunctionClosures(exports, budget);
        Assert.Same(newer, exports[("f", 0)]);
        second.CollectFunctionClosures(exports, budget);
        Assert.Same(newer, exports[("f", 0)]);
        Assert.Same(definition, second.CollectFunctions(budget)?[("f", 0)]);
    }
}
