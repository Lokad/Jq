using System.Runtime.ExceptionServices;

namespace Lokad.Jq;

internal static class JqHostExtensions
{
    // A host CLR failure (unexpected argument/overflow from IJqHost) escapes
    // the jq error boundary instead of being mislabeled as a filter or input
    // error. Cancellation and jq budgets always propagate unchanged.
    internal static async Task<T> GuardHostAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new JqHostFailureException("host operation violated its contract", exception);
        }
    }

    internal static async ValueTask<JqByteReadResult> GuardedReadAsync(
        IJqHost host, JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        Task<JqByteReadResult> operation() =>
            host.ReadBytesAsync(descriptor, buffer, cancellationToken).AsTask();
        JqByteReadResult read = await GuardHostAsync(operation).ConfigureAwait(false);
        if (!read.IsError && read.BytesRead > buffer.Length)
        {
            throw new JqHostFailureException("host read returned more bytes than requested", null);
        }

        return read;
    }

    // Read with bounded requests and publish accumulated bytes only on successful EOF.
    internal static async ValueTask<BoundedReadResult> TryReadAllBytesAsync(
        this IJqHost host, JqFileDescriptor descriptor, JqBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int maximumBytes = budget.RemainingInput;
        using var readMemory = new JqMemoryReservation(budget.MemoryBudget);
        var outputMemory = new JqMemoryReservation(budget.MemoryBudget);
        bool published = false;
        try
        {
            int initialCapacity = Math.Min(4096, maximumBytes);
            outputMemory.Resize(initialCapacity);
            using var output = new MemoryStream(initialCapacity);
            byte[] buffer = [];
            readMemory.ReplaceBuffer(ref buffer, Math.Min(8192, maximumBytes + 1));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(buffer.Length, maximumBytes - (int)output.Length + 1);
                var read = await GuardedReadAsync(host, descriptor, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (read.IsError)
                    return new BoundedReadResult.Failed(read);
                budget.ChargeInput(read.BytesRead);
                if (read.BytesRead > maximumBytes - output.Length)
                    return new BoundedReadResult.TooLarge();
                int required = (int)output.Length + read.BytesRead;
                if (required > output.Capacity)
                {
                    int previous = output.Capacity;
                    int capacity = Math.Max(required, Math.Max(256, previous * 2));
                    outputMemory.Resize((long)previous + capacity);
                    try { output.Capacity = capacity; }
                    catch
                    {
                        outputMemory.Resize(previous);
                        throw;
                    }
                    outputMemory.Resize(capacity);
                }
                output.Write(buffer.AsSpan(0, read.BytesRead));
                if (read.ReachedEoF)
                {
                    var result = new BoundedReadResult.Complete(
                        output.GetBuffer().AsMemory(0, (int)output.Length), outputMemory);
                    published = true;
                    return result;
                }
            }
        }
        finally
        {
            if (!published) outputMemory.Dispose();
        }
    }

    internal static async Task<int> AppendAsync(
        this IJqHost host, JqFileDescriptor descriptor, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        Task<JqAppendResult> operation() =>
            host.AppendWhileOpenAsync(descriptor, content, cancellationToken);
        return (await GuardHostAsync(operation).ConfigureAwait(false)).ExitCode;
    }

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
