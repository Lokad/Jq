using BenchmarkDotNet.Attributes;

namespace Lokad.Jq.Benchmarking;

[MemoryDiagnoser]
public class AgentBenchmarks
{
    private GovernorWorkload? _scenario;

    [ParamsSource(nameof(Cases))]
    public string Case { get; set; } = string.Empty;

    public IEnumerable<string> Cases => AgentWorkloads.Create()
        .Select(item => item.Workload.Name + "/" + item.Workload.Scale);

    [GlobalSetup]
    public void Setup() => _scenario = AgentWorkloads.Create()
        .Single(item => item.Workload.Name + "/" + item.Workload.Scale == Case);

    [Benchmark]
    public async Task<long> Execute()
    {
        var scenario = _scenario ?? throw new InvalidOperationException("Benchmark setup has not run.");
        var result = await BenchmarkExecution.RunAsync(scenario.Workload.Arguments,
            scenario.Workload.Input, false, CancellationToken.None).ConfigureAwait(false);
        scenario.Verify(result);
        return result.Digest.Length;
    }
}
