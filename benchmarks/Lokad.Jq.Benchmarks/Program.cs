using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Lokad.Jq;

BenchmarkSwitcher.FromAssembly(typeof(CommandBenchmarks).Assembly).Run(args);

// Representative embedding workloads. Parse-only cases isolate compilation cost;
// the rest parse once and reuse the command across iterations, which the runtime
// supports with independent per-execution budgets. Outputs drain into a discard
// host. Keep iteration bodies in the millisecond range so routine runs stay fast;
// heavyweight sweeps do not belong in ordinary CI.
[MemoryDiagnoser]
public class CommandBenchmarks
{
    private readonly DiscardHost _host = new();
    private readonly Jq _warmRange = Parse(["-n", "range(0;1000) | . * 2"]);
    private readonly Jq _scalar = Parse(["-n", "[range(0;500) | {name: .}] | .[].name"]);
    private readonly Jq _construct = Parse(["-n", "[{a: range(0;20), b: range(0;20)}]"]);
    private readonly Jq _reduce = Parse(["-n", "reduce range(0;1000) as $x (0; . + $x)"]);
    private readonly Jq _sort = Parse(["-n", "[range(0;500) | {k: . % 7, v: .}] | sort_by(.k) | group_by(.k) | map(length)"]);
    private readonly Jq _regex = Parse(["-n", "[range(0;200) | {v: .}] | map(.v | tostring | test(\"^[0-9]+$\")) | length"]);
    private readonly Jq _unicode = Parse(["-n", "[\"\u00e9\U0001F680\" * 100] | map(explode | implode) | length"]);
    private readonly Jq _walk = Parse(["-n", "[{a: {b: [range(0;50)]}}] | walk(if type == \"number\" then . + 1 else . end) | length"]);
    private readonly Jq _descent = Parse(["-n", "[range(0;200) | {v: .}] | [paths] | length"]);

    [Benchmark]
    public Jq? ParseOnly() =>
        Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-n", "range(0;100) | {value:.}"], []));

    [Benchmark]
    public Task<int> ParseAndExecute() =>
        (Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-n", "range(0;100) | {value:.}"], [])) ?? throw new InvalidOperationException("Expected jq command"))
        .ExecuteAsync(_host, CancellationToken.None);

    [Benchmark]
    public Task<int> WarmExecuteRange() => _warmRange.ExecuteAsync(_host, CancellationToken.None);

    [Benchmark]
    public Task<int> ScalarExtraction() => _scalar.ExecuteAsync(_host, CancellationToken.None);

    [Benchmark]
    public Task<int> ConstructionProducts() => _construct.ExecuteAsync(_host, CancellationToken.None);

    [Benchmark]
    public Task<int> ReductionFold() => _reduce.ExecuteAsync(_host, CancellationToken.None);
    [Benchmark]
    public Task<int> SortGroup() => _sort.ExecuteAsync(_host, CancellationToken.None);

    [Benchmark]
    public Task<int> RegexMatch() => _regex.ExecuteAsync(_host, CancellationToken.None);

    [Benchmark]
    public Task<int> UnicodeExplode() => _unicode.ExecuteAsync(_host, CancellationToken.None);

    [Benchmark]
    public Task<int> WalkRebuild() => _walk.ExecuteAsync(_host, CancellationToken.None);

    [Benchmark]
    public Task<int> DescentPaths() => _descent.ExecuteAsync(_host, CancellationToken.None);

    private static Jq Parse(string[] args) =>
        Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", args, [])) ?? throw new InvalidOperationException("Expected jq command");

    private sealed class DiscardHost : IJqHost
    {
        public ValueTask<JqByteReadResult> ReadBytesAsync(
            JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(JqByteReadResult.EndOfFile);

        public Task<JqAppendResult> AppendWhileOpenAsync(
            JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            Task.FromResult(JqAppendResult.Open);

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
            Task.FromResult(JqOpenedFile.Failure("No files in benchmark host"));

        public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
