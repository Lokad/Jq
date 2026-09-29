using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Lokad.Jq;

BenchmarkSwitcher.FromAssembly(typeof(CommandBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class CommandBenchmarks
{
    private readonly JqCommandInvocation _invocation =
        JqCommandInvocation.CreateWithStandardDescriptors("jq", ["-n", "range(0;100) | {value:.}"], []);
    private readonly DiscardHost _host = new();

    [Benchmark]
    public Task<int> ParseAndExecute() =>
        (Jq.TryParse(_invocation) ?? throw new InvalidOperationException("Expected jq command"))
        .ExecuteAsync(_host, CancellationToken.None);

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
