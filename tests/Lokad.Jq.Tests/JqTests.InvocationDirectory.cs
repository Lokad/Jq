using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    [InlineData(16384, false)]
    [InlineData(16384, true)]
    public void Jq_InvocationDirectoryAdmitsOnlyItsBorrowedWrapper(int length, bool valid)
    {
        string pwd = (valid ? "/" : "") + new string('x', length);
        var root = new BenchmarkMemoryBudget(1024);
        var memory = new CountingMemoryBudget(root);
        Assert.True(JqCommandInvocation.TryCreateWithStandardDescriptors("jq", ["-n", "42"], [new("PWD", pwd)], memory, out var invocation));
        using (invocation)
        {
            Assert.Equal(valid ? pwd : "/", invocation.CurrentDirectory.Path);
            if (valid) Assert.Same(pwd, invocation.CurrentDirectory.Path);
            Assert.True(root.ReservedBytes <= 512);
            Assert.Equal(1, memory.Counts.Reservations);
            Assert.Equal(0, memory.Counts.Refusals);
        }
        Assert.Equal(0, root.ReservedBytes);
        Assert.Equal(0, root.ActiveScopes);
    }

    [Fact]
    public void Jq_InvocationDirectoryUsesTheFirstExportEvenWhenInvalid()
    {
        Assert.Same(JqPath.Root, JqPathResolution.ResolveCurrentDirectory([new("PWD", "invalid"), new("PWD", "/work")]));
        Assert.Equal("/first", JqPathResolution.ResolveCurrentDirectory([new("PWD", "/first"), new("PWD", "/second")]).Path);
    }
}
