using System.Text.Json.Nodes;
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
    [InlineData("label $out | def f: break $out; (label $out | try f catch 99),42", "99\n42\n")]
    [InlineData("label $out | def f: break $out; (label $out | f?),42", "42\n")]
    [InlineData("label $out | try break $out catch .", "{\"__jq\":0}\n")]
    [InlineData("label $out | label $inner | try break $inner catch .", "{\"__jq\":1}\n")]
    [InlineData("label $out | try break $out catch error(.)", "")]
    [InlineData("label $out | error({__jq:0})", "")]
    [InlineData("label $out | def f: break $out; path(try f catch empty)", "")]
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

    [Theory]
    [InlineData("label $out | .")]
    [InlineData("label $out | break $out")]
    public async Task Jq_CompletedLabelMetadataReusesRecordCredit(string body)
    {
        var host = new MockFileSystem();
        var root = new BenchmarkMemoryBudget(64 * 1024);
        var memory = new CountingMemoryBudget(root);
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", "-nc",
            "reduce range(100000) as $i (0; " + body + ")")));
        Assert.Equal(0, await command.ExecuteAsync(host, memory, CancellationToken.None));
        Assert.Equal(body.EndsWith(" .", StringComparison.Ordinal) ? "0\n" : "null\n", host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdErr));
        Assert.InRange(memory.Counts.Reservations + memory.Counts.Resizes, 1, 100);
        Assert.Equal(0, memory.Counts.BytesReads);
        Assert.Equal(0, memory.Counts.SameSizeResizes);
        Assert.Equal(0, root.ReservedBytes);
    }
    [Fact]
    public void Jq_CaughtLabelPayloadAdmissionIsTerminal()
    {
        using var compiler = new JqContext(new Dictionary<string, JsonNode?>(), new JqBudget(CancellationToken.None));
        var filter = new JqParser("label $out | try break $out catch 42", JqProgramSource.Inline,
            compiler.RootEnvironment, compiler.Budget).Parse();
        var root = new BenchmarkMemoryBudget(128);
        using (var context = new JqContext(new Dictionary<string, JsonNode?>(),
            new JqBudget(JqUtf8RegexEngine.Instance, root, CancellationToken.None)))
        {
            var error = Assert.Throws<JqQuotaException>(() => filter.Evaluate(null, context, context.RootEnvironment).ToArray());
            Assert.True(error.IsMemoryAdmission);
        }
        Assert.Equal(0, root.ReservedBytes);
    }

}
