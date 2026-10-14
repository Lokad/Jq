using System.Text;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal static class JqDiagnosticWriter
{
    // Small immutable emergency output belongs to library metadata, outside
    // workload storage. Refusal must remain observable when the root is full.
    private static readonly ReadOnlyMemory<byte> Refusal = "jq: memory budget exceeded\n"u8.ToArray();

    internal static async Task<bool> TryWriteAsync(IJqHost host, JqFileDescriptor descriptor,
        IReadOnlyList<string> parts, bool newline, IMemoryBudget? memoryBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long length = newline ? 1 : 0;
        foreach (string part in parts) length += Encoding.UTF8.GetByteCount(part);
        using var memory = new JqMemoryReservation(memoryBudget);
        if (length > int.MaxValue || !memory.TryResize(length))
        {
            await host.AppendAsync(descriptor, Refusal, cancellationToken).ConfigureAwait(false);
            return false;
        }
        var bytes = new byte[(int)length];
        int offset = 0;
        foreach (string part in parts)
            offset += Utf8Text.Encode(part, bytes.AsSpan(offset));
        if (newline) bytes[offset] = (byte)'\n';
        cancellationToken.ThrowIfCancellationRequested();
        await host.AppendAsync(descriptor, bytes, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
