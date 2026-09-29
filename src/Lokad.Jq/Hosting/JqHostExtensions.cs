using System.Runtime.ExceptionServices;

namespace Lokad.Jq;

internal static class JqHostExtensions
{
    // Read with bounded requests and publish accumulated bytes only on successful EOF.
    internal static async ValueTask<BoundedReadResult> TryReadAllBytesAsync(
        this IJqHost host, JqFileDescriptor descriptor, int maximumBytes,
        Action<int> chargeRead, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream(Math.Min(4096, maximumBytes));
        var buffer = new byte[Math.Min(8192, maximumBytes + 1)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(buffer.Length, maximumBytes - (int)output.Length + 1);
            var read = await host.ReadBytesAsync(descriptor, buffer.AsMemory(0, length), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (read.IsError)
                return new BoundedReadResult.Failed(read);
            chargeRead(read.BytesRead);
            cancellationToken.ThrowIfCancellationRequested();
            if (read.BytesRead > maximumBytes - output.Length)
                return new BoundedReadResult.TooLarge();
            output.Write(buffer.AsSpan(0, read.BytesRead));
            if (read.ReachedEoF)
                return new BoundedReadResult.Complete(output.GetBuffer().AsMemory(0, (int)output.Length));
        }
    }

    internal static async Task<int> AppendAsync(
        this IJqHost host, JqFileDescriptor descriptor, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        (await host.AppendWhileOpenAsync(descriptor, content, cancellationToken).ConfigureAwait(false)).ExitCode;

    internal static async Task CloseOwnedDescriptorAsync(
        this IJqHost host, JqFileDescriptor descriptor, Exception? earlierException)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (await host.CloseDescriptorAsync(descriptor, CancellationToken.None).ConfigureAwait(false) == 0)
                    return;
            }
            catch (Exception exception)
            {
                if (earlierException == null) ExceptionDispatchInfo.Capture(exception).Throw();
                return;
            }
        }
        if (earlierException != null) return;
        throw new JqException("input close failed");
    }
}
