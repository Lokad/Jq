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
        var memory = new TestMemoryBudget(32 * 1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("5000\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_BindsAdmittedArgumentVectors()
    {
        string[] arguments = ["-n", "--args", "$ARGS.positional | length", .. Enumerable.Repeat("value", 5000)];
        var memory = new TestMemoryBudget(4 * 1024 * 1024);
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", arguments, [])));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
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
        Assert.Equal(3, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("function has too many parameters", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Fact]
    public async Task Jq_TailRewriteGuardsItsActualRecursiveDepth()
    {
        string filter = "def f: " + string.Concat(Enumerable.Range(0, 300).Select(index => "def g" + index + ":0;")) + "f; 0";
        var host = new MockFileSystem();
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-n", filter)));
        Assert.Equal(3, await command.ExecuteAsync(host, CancellationToken.None));
        Assert.Contains("filter nesting limit exceeded", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jq_LargeCompilationUsesSharedAdmissionAndCleansOwnedFiles(bool hosted)
    {
        string source = "{" + string.Join(',', Enumerable.Range(0, 5000).Select(index => "k" + index + ":0")) + "}";
        var memory = new TestMemoryBudget(64 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/program", source);
        var invocation = hosted ? BuildInvocation("jq", "-n", "-f", "/program") : BuildInvocation("jq", "-n", source);
        var command = Assert.IsType<Jq>(Jq.TryParse(invocation));
        Assert.Equal(5, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("jq: memory budget exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(hosted ? 1 : 0, host.ClosedDescriptors.Count);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
