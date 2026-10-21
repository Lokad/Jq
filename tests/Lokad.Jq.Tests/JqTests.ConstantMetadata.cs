using System.Text.Json.Nodes;
using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(1000)]
    [InlineData(2000)]
    public async Task Jq_ConstantMetadataAdmissionScalesWithItsStorage(int count)
    {
        string source = "module {items:[" + string.Join(',', Enumerable.Repeat("[0]", count)) + "]}; 0";
        var root = new BenchmarkMemoryBudget(4 * 1024 * 1024);
        var memory = new CountingMemoryBudget(root);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 1000);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ConstantMetadataKeepsNestedCommaOrderAndDuplicateKeys()
    {
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/lib/m.jq", "module {a:[1,[2,3],{b:[4]}],dup:1,dup:2}; def f:42;");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/lib",
            "[(\"m\"|modulemeta).a,(\"m\"|modulemeta).dup]")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("[[1,[2,3],{\"b\":[4]}],2]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Fact]
    public async Task Jq_ConstantMetadataResultsStayIndependentOfTheCache()
    {
        var root = new BenchmarkMemoryBudget(256 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/lib/m.jq", "module {a:[1,2]}; def f:42;");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", "-L", "/lib",
            "(\"m\"|modulemeta) as $m | [($m.a|.[0]=9),(\"m\"|modulemeta).a,$m.a]")));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal("[[9,2],[1,2],[1,2]]\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Single(host.ClosedDescriptors);
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData("module {a:.};0")]
    [InlineData("module {a:[0,.]};0")]
    [InlineData("module {(.):1};0")]
    public async Task Jq_MetadataStillRejectsInputDependentExpressions(string source)
    {
        var host = new MockFileSystem();
        var root = new BenchmarkMemoryBudget(128 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(3, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Contains("Module metadata must be constant", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Jq_ConstantMetadataPreservesRefusalAndCancellation(bool cancel)
    {
        using var compiler = new JqBudget(CancellationToken.None);
        IReadOnlyList<Token> tokens = Lexer.Tokenize("module {a:[1,[2,3]]};0", JqProgramSource.Inline, compiler);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        var root = new BenchmarkMemoryBudget(0);
        using (var budget = new JqBudget(JqUtf8RegexEngine.Instance, root, cancellation.Token))
        {
            var parser = new JqParser(tokens, JqProgramSource.Inline,
                JqEnvironment.CreateRoot(new Dictionary<string, JsonNode?>()), budget);
            if (cancel) Assert.ThrowsAny<OperationCanceledException>(() => parser.ParsePrefix());
            else Assert.True(Assert.Throws<JqQuotaException>(() => parser.ParsePrefix()).IsMemoryAdmission);
        }
        Assert.Equal(0, root.ReservedBytes);
    }
}
