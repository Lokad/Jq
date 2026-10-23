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
        var capture = root.Extend("x", JsonValue.Create(42)).ExtendFunction("f", 0, definition);
        JqEnvironment environment = capture;
        string alias = new('m', aliasLength);
        for (int i = 0; i < depth; i++) environment = root.ExtendModule(alias, environment);
        string qualified = string.Join("::", Enumerable.Repeat(alias, depth)) + "::f";
        JqUserClosure? closure = null;
        for (int i = 0; i < 100; i++) environment.TryGetFunctionQualified(qualified, 0, out closure);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            if (!environment.TryGetFunctionQualified(qualified, 0, out closure))
                throw new InvalidOperationException("Expected the qualified definition.");
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64000);
        Assert.NotNull(closure);
        Assert.Same(definition, closure.Definition);
        Assert.Same(capture, closure.Environment);
        Assert.False(environment.TryGetFunctionQualified(qualified, 1, out _));
        Assert.False(environment.TryGetFunctionQualified(qualified.ToUpperInvariant(), 0, out _));
        Assert.False(environment.TryGetFunctionQualified(alias + "::", 0, out _));
        Assert.False(environment.TryGetFunctionQualified("::f", 0, out _));
    }
}
