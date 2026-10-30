namespace Lokad.Jq;

internal static class JqHostExtensions
{
    // An unexpected host CLR failure escapes
    // the jq error boundary instead of being mislabeled as a filter or input
    // error. Cancellation and jq budgets always propagate unchanged.
    internal static async ValueTask<JqAppendResult> GuardedAppendAsync(
        IJqHost host, JqFileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        try { return await host.AppendWhileOpenAsync(descriptor, content, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
        { throw new JqHostFailureException("host operation violated its contract", exception); }
    }

    internal static async ValueTask<JqOpenedFile> GuardedOpenAsync(
        IJqHost host, JqPathRequest path, CancellationToken cancellationToken)
    {
        try
        {
            var opened = await host.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (opened.FileDescriptor is null && opened.Error is null)
                throw new JqHostFailureException("host open returned neither a descriptor nor an error", null);
            return opened;
        }
        catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
        { throw new JqHostFailureException("host operation violated its contract", exception); }
    }

    internal static async ValueTask<JqByteReadResult> GuardedReadAsync(
        IJqHost host, JqFileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        JqByteReadResult read;
        try { read = await host.ReadBytesAsync(descriptor, buffer, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
        { throw new JqHostFailureException("host operation violated its contract", exception); }
        if (read.BytesRead > buffer.Length)
        {
            throw new JqHostFailureException("host read returned more bytes than requested", null);
        }

        return read;
    }

    // Read with bounded requests and publish accumulated bytes only on successful EOF.
    internal static async ValueTask<JqReadResult> TryReadAllBytesAsync(
        this IJqHost host, JqFileDescriptor descriptor, JqBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var readMemory = new JqMemoryReservation(budget.MemoryBudget);
        var outputMemory = new JqMemoryReservation(budget.MemoryBudget);
        bool published = false;
        try
        {
            const int initialCapacity = 4096;
            outputMemory.Resize(initialCapacity);
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new MemoryStream(initialCapacity);
            byte[] buffer = [];
            readMemory.ReplaceBuffer(ref buffer, 8192, cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await GuardedReadAsync(host, descriptor, buffer, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (read.Error is { } error)
                    return new JqReadResult.Failed(error);
                int required = JqBudget.BufferCapacity(output.Length + read.BytesRead, 0);
                if (required > output.Capacity)
                {
                    int previous = output.Capacity;
                    int capacity = JqBudget.BufferCapacity(required, Math.Max(256, 2L * previous));
                    outputMemory.Resize((long)previous + capacity);
                    cancellationToken.ThrowIfCancellationRequested();
                    try { output.Capacity = capacity; }
                    catch
                    {
                        outputMemory.Resize(previous);
                        throw;
                    }
                    outputMemory.Resize(capacity);
                }
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer.AsSpan(0, read.BytesRead));
                if (read.ReachedEoF)
                {
                    var result = new JqReadResult.Complete(
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

    internal static async ValueTask<int> AppendAsync(
        this IJqHost host, JqFileDescriptor descriptor, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        (await GuardedAppendAsync(host, descriptor, content, cancellationToken).ConfigureAwait(false)).ExitCode;

    internal static async Task<JqReadResult.Complete> ReadFileAsync(
        this IJqHost host, JqResolvedPath path, JqBudget budget, CancellationToken cancellationToken)
    {
        var opened = await JqHostExtensions.GuardedOpenAsync(host, path.Request, cancellationToken).ConfigureAwait(false);
        if (opened.FileDescriptor is not { } descriptor)
        {
            throw new JqOperationException("opening", path.Display,
                opened.Error ?? throw new InvalidOperationException("Missing open error."));
        }
        bool failed = false;
        JqReadResult.Complete? content = null;
        try
        {
            var result = await host.TryReadAllBytesAsync(descriptor, budget, cancellationToken).ConfigureAwait(false);
            content = result switch
            {
                JqReadResult.Complete complete => complete,
                JqReadResult.Failed failure => throw new JqOperationException("reading", path.Display, failure.Error),
                _ => throw new InvalidOperationException("Unknown bounded read result.")
            };
            return content;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            try { await host.CloseOwnedDescriptorAsync(descriptor, path.Display, failed).ConfigureAwait(false); }
            catch { content?.Dispose(); throw; }
        }
    }

    internal static async Task CloseOwnedDescriptorAsync(
        this IJqHost host, JqFileDescriptor descriptor, string path, bool earlierFailure)
    {
        JqCloseResult result;
        try
        {
            result = await host.CloseDescriptorAsync(descriptor, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (earlierFailure) return;
            if (JqErrors.IsCatchableClr(exception))
                throw new JqHostFailureException("host operation violated its contract", exception);
            throw;
        }
        if (!earlierFailure && result.Error is { } error)
            throw new JqCloseException(path, error);
    }
}
