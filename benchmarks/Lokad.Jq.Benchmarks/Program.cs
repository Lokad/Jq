using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Lokad.Jq;
using Lokad.Jq.Benchmarking;

if (args is ["--check-machine"])
{
    var check = await MachineQuietProbe.CheckAsync(5, TimeSpan.FromSeconds(1), CancellationToken.None);
    Console.WriteLine(check.Reason);
    Console.WriteLine("Background CPU samples: " + string.Join(", ", check.BusyPercent.Select(value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))) + "%");
    Environment.ExitCode = check.IsQuiet ? 0 : 3;
    return;
}
if (args is ["--render-report", var artifact])
{
    await BenchmarkReport.RenderFileAsync(artifact, CancellationToken.None);
    return;
}
if (args.Length > 0 && args[0] == "--compare")
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
    try { Environment.ExitCode = await ComparisonRunner.RunAsync(args[1..], cancellation.Token); }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled; completed case checkpoints remain in artifacts/."); Environment.ExitCode = 130; }
    catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
    { Console.Error.WriteLine("Benchmark tool failed: " + exception.Message); Environment.ExitCode = 1; }
    return;
}
if (args is ["--process-fixture", var fixture])
{
    // Development fixture for portable subprocess lifecycle tests; never jq.
    if (fixture == "wait") await Task.Delay(TimeSpan.FromMinutes(5));
    else if (fixture == "overflow")
    {
        var output = Console.OpenStandardOutput();
        byte[] buffer = new byte[8192];
        for (int i = 0; i < 6000; i++) await output.WriteAsync(buffer);
    }
    else if (fixture == "pipes")
    {
        byte[] input;
        using (var stream = new MemoryStream())
        {
            await Console.OpenStandardInput().CopyToAsync(stream);
            input = stream.ToArray();
        }
        await Task.WhenAll(Console.OpenStandardOutput().WriteAsync(input).AsTask(),
            Console.OpenStandardError().WriteAsync("diagnostic"u8.ToArray()).AsTask());
        Environment.ExitCode = 7;
    }
    else Environment.ExitCode = 2;
    return;
}

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

if (args is ["--stress"])
{
    await StressChecks.RunAsync(CancellationToken.None);
    Console.WriteLine("Bounded non-regex stress checks passed; no timings collected.");
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
