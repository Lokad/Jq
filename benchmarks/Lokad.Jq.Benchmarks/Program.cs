using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Lokad.Jq;
using Lokad.Jq.Benchmarking;

if (args is ["--smoke"])
{
    var benchmarks = new CommandBenchmarks();
    benchmarks.BindCommand();
    await benchmarks.ParseAndExecute();
    await benchmarks.ReusedCommandRange();
    await benchmarks.ScalarExtraction();
    await benchmarks.ConstructionProducts();
    await benchmarks.ReductionFold();
    await benchmarks.SortGroup();
    await benchmarks.UnicodeExplode();
    await benchmarks.WalkRebuild();
    await benchmarks.DescentPaths();
    await benchmarks.Utf8InputTransform();
    await benchmarks.Utf8UnicodeTransform();
    Console.WriteLine("12 non-regex benchmark checks passed; no timings collected.");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(CommandBenchmarks).Assembly).Run(args);

// Binding alone is measured separately. Reused commands still compile their
// filters on every ExecuteAsync. All execution cases check complete output,
// status and separate stderr against independently constructed expectations.
[MemoryDiagnoser]
public class CommandBenchmarks
{
    private readonly LocalWorkload _range = LocalWorkload.Range();
    private readonly LocalWorkload _scalar = LocalWorkload.Scalar();
    private readonly LocalWorkload _construct = LocalWorkload.Construction();
    private readonly LocalWorkload _reduce = LocalWorkload.Reduction();
    private readonly LocalWorkload _sort = LocalWorkload.SortGroup();
    private readonly LocalWorkload _unicode = LocalWorkload.Unicode();
    private readonly LocalWorkload _walk = LocalWorkload.Walk();
    private readonly LocalWorkload _descent = LocalWorkload.Paths();
    private readonly LocalWorkload _input = LocalWorkload.InputTransform();
    private readonly LocalWorkload _unicodeInput = LocalWorkload.UnicodeTransform();
    private readonly LocalWorkload _parse = LocalWorkload.ParseAndExecute();

    [Benchmark]
    public Jq BindCommand() => BenchmarkExecution.Bind(_parse.Arguments);

    [Benchmark]
    public Task<long> ParseAndExecute() => _parse.ExecuteAsync(true);

    [Benchmark]
    public Task<long> ReusedCommandRange() => _range.ExecuteAsync(false);

    [Benchmark]
    public Task<long> ScalarExtraction() => _scalar.ExecuteAsync(false);

    [Benchmark]
    public Task<long> ConstructionProducts() => _construct.ExecuteAsync(false);

    [Benchmark]
    public Task<long> ReductionFold() => _reduce.ExecuteAsync(false);

    [Benchmark]
    public Task<long> SortGroup() => _sort.ExecuteAsync(false);

    [Benchmark]
    public Task<long> UnicodeExplode() => _unicode.ExecuteAsync(false);

    [Benchmark]
    public Task<long> WalkRebuild() => _walk.ExecuteAsync(false);

    [Benchmark]
    public Task<long> DescentPaths() => _descent.ExecuteAsync(false);

    [Benchmark]
    public Task<long> Utf8InputTransform() => _input.ExecuteAsync(false);

    [Benchmark]
    public Task<long> Utf8UnicodeTransform() => _unicodeInput.ExecuteAsync(false);
}
