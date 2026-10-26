using System.Text.Json.Nodes;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Jq_EmptyPrefixAvoidsUnusedEnvironmentCollections(int functions)
    {
        using var budget = new JqBudget(CancellationToken.None);
        var environment = JqEnvironment.CreateRoot(new Dictionary<string, JsonNode?>());
        for (int i = 0; i < functions; i++)
            environment = environment.ExtendFunction(new JqFunctionDefinition("f" + i, [], new IdentityFilter()));
        var tokens = Lexer.Tokenize("42", JqProgramSource.Inline, budget);
        for (int i = 0; i < 100; i++) new JqParser(tokens, JqProgramSource.Inline, environment, budget).ParsePrefix();
        long allocated = 0;
        for (int i = 0; i < 100; i++)
        {
            var parser = new JqParser(tokens, JqProgramSource.Inline, environment, budget);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var prefix = parser.ParsePrefix();
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Null(prefix.Metadata);
            Assert.Empty(prefix.Imports);
            Assert.Equal(0, prefix.BodyIndex);
        }
        Assert.Equal(0, allocated);
        if (functions != 0)
            Assert.IsType<UserCallFilter>(new JqParser("f0", JqProgramSource.Inline, environment, budget).Parse());
    }

    [Fact]
    public void Jq_EmptyPrefixStillObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(cancellation.Token);
        var environment = JqEnvironment.CreateRoot(new Dictionary<string, JsonNode?>());
        var parser = new JqParser("42", JqProgramSource.Inline, environment, budget);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => parser.ParsePrefix());
    }
}
