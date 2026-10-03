using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Lokad.Jq;

BenchmarkSwitcher.FromAssembly(typeof(CommandBenchmarks).Assembly).Run(args);

// Representative embedding workloads. Jq.TryParse binds the command line only
// (argument classification, descriptors, environment snapshots); filter parsing
// and compilation happen on every ExecuteAsync inside JqExecutor, so reusing a
// Jq command across iterations amortizes binding but still recompiles the
// filter per execution. BindCommand isolates binding cost; ParseAndExecute
// covers bind plus compile plus execute; ReusedCommand* cases amortize binding
// only. The -n cases generate values internally and drain into a discard host.
// Utf8* cases feed real UTF-8 bytes through stdin, transform them, and capture
// rendered output, validating exit 0 and nonempty output per iteration so empty
// or failing runs cannot silently pass as fast. MemoryDiagnoser records
// allocations for every benchmark. Keep iteration bodies in the millisecond
// range so routine runs stay fast; heavyweight sweeps do not belong in
// ordinary CI.
[MemoryDiagnoser]
public class CommandBenchmarks
{
    private readonly DiscardHost _host = new();
    private readonly Jq _reusedRange = Parse(["-n", "range(0;1000) | . * 2"]);
    private readonly Jq _scalar = Parse(["-n", "[range(0;500) | {name: .}] | .[].name"]);
    private readonly Jq _construct = Parse(["-n", "[{a: range(0;20), b: range(0;20)}]"]);
    private readonly Jq _reduce = Parse(["-n", "reduce range(0;1000) as $x (0; . + $x)"]);
    private readonly Jq _sort = Parse(["-n", "[range(0;500) | {k: . % 7, v: .}] | sort_by(.k) | group_by(.k) | map(length)"]);
    private readonly Jq _regex = Parse(["-n", "[range(0;200) | {v: .}] | map(.v | tostring | test(\"^[0-9]+$\")) | length"]);
    private readonly Jq _unicode = Parse(["-n", "[\"\u00e9\U0001F680\" * 100] | map(explode | implode) | length"]);
    private readonly Jq _walk = Parse(["-n", "[{a: {b: [range(0;50)]}}] | walk(if type == \"number\" then . + 1 else . end) | length"]);
    private readonly Jq _descent = Parse(["-n", "[range(0;200) | {v: .}] | [paths] | length"]);
    private readonly Jq _inputTransform = Parse(["-c", "map(.v * 2)"]);
    private readonly Jq _unicodeTransform = Parse(["-c", "map(.t | explode | implode)"]);

    private readonly byte[] _objectArrayInput = BuildObjectArrayInput(1000);
    private readonly byte[] _unicodeArrayInput = BuildUnicodeArrayInput(200);

    [Benchmark]
    public Jq? BindCommand() =>
        Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-n", "range(0;100) | {value:.}"], []));

    [Benchmark]
    public Task<int> ParseAndExecute() =>
        (Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-n", "range(0;100) | {value:.}"], [])) ?? throw new InvalidOperationException("Expected jq command"))
        .ExecuteAsync(_host, CancellationToken.None);

    [Benchmark]
    public Task<int> ReusedCommandRange() => _reusedRange.ExecuteAsync(_host, CancellationToken.None);

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

    [Benchmark]
    public async Task<long> Utf8InputTransform()
    {
        var host = new InputCaptureHost(_objectArrayInput);
        int exit = await _inputTransform.ExecuteAsync(host, CancellationToken.None).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException("Utf8InputTransform exited with " + exit + ".");
        }

        long bytes = host.OutputByteCount;
        if (bytes <= 0)
        {
            throw new InvalidOperationException("Utf8InputTransform produced no output.");
        }

        return bytes;
    }

    [Benchmark]
    public async Task<long> Utf8UnicodeTransform()
    {
        var host = new InputCaptureHost(_unicodeArrayInput);
        int exit = await _unicodeTransform.ExecuteAsync(host, CancellationToken.None).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException("Utf8UnicodeTransform exited with " + exit + ".");
        }

        long bytes = host.OutputByteCount;
        if (bytes <= 0)
        {
            throw new InvalidOperationException("Utf8UnicodeTransform produced no output.");
        }

        return bytes;
    }

    private static Jq Parse(string[] args) =>
        Jq.TryParse(JqCommandInvocation.CreateWithStandardDescriptors("jq", args, [])) ?? throw new InvalidOperationException("Expected jq command");

    private static byte[] BuildObjectArrayInput(int count)
    {
        var builder = new StringBuilder(count * 10);
        builder.Append((char)91);
        for (int index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append((char)44);
            }

            builder.Append("{\"v\":").Append(index).Append((char)125);
        }

        builder.Append((char)93);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] BuildUnicodeArrayInput(int count)
    {
        var builder = new StringBuilder(count * 24);
        builder.Append((char)91);
        for (int index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append((char)44);
            }

            builder.Append("{\"t\":\"caf\u00e9 \U0001F680 item-").Append(index).Append("\"}");
        }

        builder.Append((char)93);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

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

    private sealed class InputCaptureHost : IJqHost
    {
        private ReadOnlyMemory<byte> _remaining;
        private readonly MemoryStream _output = new();

        public InputCaptureHost(byte[] input)
        {
            _remaining = input;
        }

        public long OutputByteCount => _output.Length;

        public ValueTask<JqByteReadResult> ReadBytesAsync(
            JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (descriptor != JqFileDescriptor.StdIn || _remaining.IsEmpty)
            {
                return ValueTask.FromResult(JqByteReadResult.EndOfFile);
            }

            int count = Math.Min(_remaining.Length, buffer.Length);
            _remaining.Slice(0, count).CopyTo(buffer);
            bool atEnd = count == _remaining.Length;
            _remaining = _remaining.Slice(count);
            return ValueTask.FromResult(JqByteReadResult.Success(count, atEnd));
        }

        public Task<JqAppendResult> AppendWhileOpenAsync(
            JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            _output.Write(content.Span);
            return Task.FromResult(JqAppendResult.Open);
        }

        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
            Task.FromResult(JqOpenedFile.Failure("No files in benchmark host"));

        public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
