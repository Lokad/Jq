using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData("label $out | def f: break $out; (label $out | f),42", "")]
    [InlineData("label $out | def f: break $out; (label $out | 1,f),42", "1\n")]
    [InlineData("label $out | def f: break $out; (label $out | \"\\(f)\"),42", "")]
    [InlineData("def f(g): (label $out | g),42; label $out | f(break $out)", "")]
    [InlineData("def f($n; g): label $out | if $n==0 then g else (f(0; break $out),42) end; f(1;.)", "")]
    [InlineData("def f($n): label $out | if $n==0 then break $out else (f(0),42) end; f(1)", "42\n")]
    [InlineData("(label $out | 1, break $out), (label $out | 2, break $out)", "1\n2\n")]
    [InlineData("label $out | def f: break $out; (label $out | f),42 | path(.)", "")]
    public async Task Jq_LabelsPreserveLexicalBindingAndActivation(string source, string expected)
    {
        var host = new MockFileSystem();
        var root = new BenchmarkMemoryBudget(1024 * 1024);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc", source)));
        Assert.Equal(0, await command.ExecuteAsync(host, root, CancellationToken.None));
        Assert.Equal(expected, host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Jq_CompletedLabelMetadataReusesRecordCredit()
    {
        var host = new MockFileSystem();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "reduce range(100000) as $i (0; label $out | .)")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal("0\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 100);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }
}
