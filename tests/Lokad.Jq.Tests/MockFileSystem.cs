using System.Text;

namespace Lokad.Jq.Tests;

// Small in-memory host for the extracted command tests. No shell parser or real IO.
internal sealed class MockFileSystem : IJqHost
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<JqFileDescriptor, ReadOnlyMemory<byte>> _inputs = new();
    private readonly Dictionary<JqFileDescriptor, MemoryStream> _outputs = new();
    private int _nextDescriptor = 3;

    public int OpenFileCount => _inputs.Keys.Count(key => key.Id >= 3);
    public int ReadBytesCallCount { get; private set; }
    public int AppendCallCount { get; private set; }
    public bool AppendRemainsOpen { get; init; } = true;
    public Action<Memory<byte>>? BeforeByteRead { get; set; }
    public Action? BeforeByteAppend { get; set; }
    public int CloseFailuresRemaining { get; set; }
    public List<JqFileDescriptor> ClosedDescriptors { get; } = [];

    public void AddFile(string path, string content) => _files[path] = Encoding.UTF8.GetBytes(content);
    public void SetStandardInput(string content) => SetStandardInputBytes(Encoding.UTF8.GetBytes(content));
    public void SetStandardInputBytes(byte[] content) => _inputs[JqFileDescriptor.StdIn] = content;
    public byte[] GetOutputBytes(JqFileDescriptor descriptor) =>
        _outputs.TryGetValue(descriptor, out var output) ? output.ToArray() : [];
    public string GetOutput(JqFileDescriptor descriptor) => Encoding.UTF8.GetString(GetOutputBytes(descriptor));

    public ValueTask<JqByteReadResult> ReadBytesAsync(
        JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ReadBytesCallCount++;
        BeforeByteRead?.Invoke(buffer);
        cancellationToken.ThrowIfCancellationRequested();
        var content = _inputs.GetValueOrDefault(descriptor);
        if (content.IsEmpty) return ValueTask.FromResult(JqByteReadResult.EndOfFile);
        var count = Math.Min(content.Length, buffer.Length);
        content[..count].CopyTo(buffer);
        _inputs[descriptor] = content[count..];
        return ValueTask.FromResult(JqByteReadResult.Success(count, reachedEoF: count == content.Length));
    }

    public Task<JqAppendResult> AppendWhileOpenAsync(
        JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        BeforeByteAppend?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        AppendCallCount++;
        if (!_outputs.TryGetValue(descriptor, out var output))
            _outputs[descriptor] = output = new MemoryStream();
        output.Write(content.Span);
        return Task.FromResult(AppendRemainsOpen ? JqAppendResult.Open : JqAppendResult.Closed);
    }

    public Task<JqOpenedFile> OpenReadAsync(JqPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_files.TryGetValue(path.Path, out var content))
            return Task.FromResult(JqOpenedFile.Failure("file not found"));
        var descriptor = new JqFileDescriptor(_nextDescriptor++);
        _inputs[descriptor] = content;
        return Task.FromResult(JqOpenedFile.Success(descriptor));
    }

    public Task<int> CloseDescriptorAsync(JqFileDescriptor descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClosedDescriptors.Add(descriptor);
        if (CloseFailuresRemaining > 0)
        {
            CloseFailuresRemaining--;
            return Task.FromResult(1);
        }
        _inputs.Remove(descriptor);
        return Task.FromResult(0);
    }
}
