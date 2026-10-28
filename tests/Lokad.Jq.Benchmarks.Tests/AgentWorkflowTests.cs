using Lokad.Jq.Benchmarking;

namespace Lokad.Jq.Tests;

public sealed class AgentWorkflowTests
{
    [Fact]
    public async Task CompoundScenariosCheckIndependentExpectedOutputAtEveryScale()
    {
        var scenarios = AgentWorkloads.Create();
        Assert.Equal(24, scenarios.Count);
        Assert.Equal(24, scenarios.Select(item => (item.Workload.Name, item.Workload.Scale)).Distinct().Count());
        foreach (var scenario in scenarios)
        {
            var result = await BenchmarkExecution.RunAsync(scenario.Workload.Arguments,
                scenario.Workload.Input, false, CancellationToken.None);
            scenario.Verify(result);
        }
    }
}
