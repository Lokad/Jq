using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace Lokad.Jq.Benchmarking;

// Separate scaling diagnostics; these do not alter the jq comparison corpus.
[MemoryDiagnoser]
public class TokenBenchmarks
{
    private byte[] _input = [];
    private Jq? _command;
    private OutputDigest? _expected;

    [Params(131072, 262144, 524288, 1048576)]
    public int InputBytes { get; set; }

    [Params(256, 8192)]
    public int ReadChunk { get; set; }

    [Params(false, true)]
    public bool Escaped { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(InputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReadChunk);
        int characters = Escaped ? InputBytes / 6 : InputBytes;
        string content = Escaped ? string.Concat(Enumerable.Repeat("\\u0061", characters)) : new string('a', characters);
        _input = Encoding.UTF8.GetBytes("\"" + content + "\"");
        _expected = OutputDigest.FromBytes(Encoding.UTF8.GetBytes(characters.ToString(CultureInfo.InvariantCulture) + "\n"));
        _command = BenchmarkExecution.Recognize(["-c", "length"]);
    }

    [Benchmark]
    public async Task<long> Execute()
    {
        var command = _command ?? throw new InvalidOperationException("Benchmark setup has not run.");
        var expected = _expected ?? throw new InvalidOperationException("Benchmark setup has not run.");
        using var inner = new BenchmarkHost(_input, false);
        int status = await command.ExecuteAsync(new FragmentedHost(inner, ReadChunk), CancellationToken.None).ConfigureAwait(false);
        var result = inner.Finish(status);
        BenchmarkExecution.Verify(result, expected);
        return result.Digest.Length;
    }

    private sealed class FragmentedHost(IJqHost inner, int chunk) : IJqHost
    {
        public ValueTask<JqByteReadResult> ReadBytesAsync(JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken) =>
            inner.ReadBytesAsync(descriptor, buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
        public Task<JqAppendResult> AppendWhileOpenAsync(JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            inner.AppendWhileOpenAsync(descriptor, content, cancellationToken);
        public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(path, cancellationToken);
        public Task<JqCloseResult> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken) =>
            inner.CloseDescriptorAsync(descriptor, cancellationToken);
    }
}
