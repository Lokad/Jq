namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Fact]
    public void Jq_AllocationWorkAndLiveStorageAdmissionHaveIndependentCounters()
    {
        var memory = new TestMemoryBudget(6);
        using var scope = memory.CreateScope();
        using var budget = new JqBudget(new JqExecutionPolicy { MaximumAllocationWorkBytes = 4 }, scope, CancellationToken.None);
        budget.ChargeAllocationWork(4);
        Assert.Equal(0, memory.ReservedBytes);
        budget.ReserveBytes(3);
        Assert.Equal(3, memory.ReservedBytes);
        Assert.Equal("allocation work allowance exceeded", Assert.Throws<JqQuotaException>(() =>
            budget.ChargeAllocationWork(1)).Message);
        Assert.Equal(3, memory.ReservedBytes);
        budget.BeginRecord();
        Assert.Equal("memory budget exceeded", Assert.Throws<JqQuotaException>(() =>
            budget.ReserveBytes(4)).Message);
        Assert.Equal(3, memory.ReservedBytes);
        budget.ReserveBytes(2);
        Assert.Equal(5, memory.ReservedBytes);
        budget.BeginRecord();
        Assert.Equal(5, memory.ReservedBytes); // Two bytes of reusable record credit remain charged.
        Assert.Equal("allocation work allowance exceeded", Assert.Throws<JqQuotaException>(() =>
            budget.ChargeAllocationWork(1)).Message);
        budget.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task Jq_AllocationWorkFailureUsesDistinctTerminalDiagnosticWithSharedMemory()
    {
        var memory = new TestMemoryBudget(1024 * 1024);
        var host = new MockFileSystem();
        host.AddFile("/input", "1\n");
        var command = Assert.IsType<Jq>(Jq.TryParse(BuildInvocation("jq", ".", "/input")));

        Assert.Equal(5, await command.ExecuteAsync(host,
            new JqExecutionPolicy { MaximumAllocationWorkBytes = 1 }, memory, CancellationToken.None));
        Assert.Equal("jq: allocation work allowance exceeded\n", host.GetOutput(JqFileDescriptor.StdErr));
        Assert.Empty(host.GetOutput(JqFileDescriptor.StdOut));
        Assert.Equal(0, host.ReadBytesCallCount);
        Assert.Empty(host.ClosedDescriptors);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void Jq_LegacyAllocationPropertyRemainsAnExplicitWorkAlias()
    {
#pragma warning disable CS0618 // Pin the deprecated public compatibility alias until its documented retirement.
        var policy = new JqExecutionPolicy { MaximumAllocationBytes = 3 };
        Assert.Equal(3, policy.MaximumAllocationWorkBytes);
        Assert.Equal(3, policy.MaximumAllocationBytes);
        var copy = policy with { MaximumAllocationWorkBytes = 5 };
        Assert.Equal(5, copy.MaximumAllocationBytes);
        Assert.Equal(3, policy.MaximumAllocationWorkBytes);
#pragma warning restore CS0618
    }
}
