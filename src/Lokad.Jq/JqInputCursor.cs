using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

// Shared pull cursor over every input source for one execution. Implicit
// outer iteration and explicit `input`/`inputs` calls draw from the same
// cursor, so values interleave exactly like the reference: each pull
// consumes the next value or line and advances filename/line state.
//
// Sources resolve once up front: no file operands means one stdin source,
// `-` operands reuse the borrowed stdin descriptor at its current position
// and are never closed, and every other operand opens lazily on first
// demand so early termination never touches later files. File descriptors
// obtained here are owned until the source is exhausted, the pull fails,
// or the cursor is disposed; stdin is always borrowed. Bytes read through
// the cursor charge the shared input budget cumulatively across sources.
//
// Reads are chunked (8 KiB): JSON values and raw lines may span chunk
// boundaries and very long scalars accumulate within the input limit. JSON
// decoding reuses the whole-buffer value reader; a decode failure with more
// bytes available retries after another chunk, while the same failure at
// end-of-source surfaces as the input error. Slurp stays an explicit
// aggregating loop over this cursor in the executor.
internal sealed class JqInputCursor : IAsyncDisposable
{
    private const int ChunkSize = 8192;

    private readonly IJqHost _host;
    private readonly JqBudget _budget;
    private readonly JqRuntime _runtime;
    private readonly JqContext _context;
    private readonly JqFileDescriptor _stdin;
    private readonly bool _raw;
    private readonly List<Source> _sources;
    private readonly CancellationToken _cancellationToken;
    private readonly byte[] _readBuffer = new byte[ChunkSize];

    private int _index = -1;
    private bool _active;
    private byte[] _buffer = [];
    private int _start;
    private int _count;
    private bool _eof;
    private JqFileDescriptor? _owned;
    private int _newlines;
    private bool _disposed;

    private sealed record Source(string Name, bool IsStdin, JqPath? Path, string Display);

    internal JqInputCursor(
        IJqHost host,
        JqInvocation invocation,
        JqContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(context);
        _host = host;
        _budget = context.Budget;
        _runtime = context.Runtime;
        _context = context;
        _stdin = invocation.StdIn;
        _raw = invocation.RawInput;
        _cancellationToken = cancellationToken;
        _sources = [];
        if (invocation.InputFiles.Count == 0)
        {
            _sources.Add(new Source("<stdin>", true, null, "<stdin>"));
        }
        else
        {
            foreach (JqResolvedPath file in invocation.InputFiles)
            {
                string display = Utf8Text.Decode(file.Display);
                if (display == "-")
                    _sources.Add(new Source("<stdin>", true, null, "-"));
                else
                    _sources.Add(new Source(display, false, file.Absolute, display));
            }
        }
    }

    internal string? LastName { get; private set; }

    internal int LastLine { get; private set; }

    // Async pull for outer iteration. Returns false when every source is
    // exhausted; otherwise sets filename/line state and returns the value.
    internal async Task<(bool HasValue, JsonNode? Value)> PullAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return _raw ? await PullRawAsync().ConfigureAwait(false) : await PullJsonAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (ShouldAbandonSource(exception))
        {
            await AbandonCurrentAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    // Synchronous pull for `input`/`inputs` filters, which evaluate without
    // an async boundary. Blocks on the same host reads the async path uses;
    // cancellation, quotas, and language errors propagate unchanged.
    internal bool TryPullSync(out JsonNode? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            (bool hasValue, JsonNode? pulled) = PullAsync().GetAwaiter().GetResult();
            value = pulled;
            return hasValue;
        }
        catch (Exception exception) when (ShouldAbandonSource(exception))
        {
            AbandonCurrentAsync(exception).GetAwaiter().GetResult();
            throw;
        }
    }

    private static bool ShouldAbandonSource(Exception exception) =>
        exception is not OperationCanceledException
        && exception is not JqQuotaException
        && exception is not JqHostFailureException;

    private async Task<(bool HasValue, JsonNode? Value)> PullJsonAsync()
    {
        while (true)
        {
            _budget.CheckCancellation();
            if (!_active && !await ActivateNextAsync().ConfigureAwait(false))
                return (false, null);
            SkipWhitespace();
            if (_count == 0)
            {
                if (_eof)
                {
                    await CloseAndAdvanceAsync(null).ConfigureAwait(false);
                    continue;
                }
                await FillAsync().ConfigureAwait(false);
                continue;
            }
            JsonNode? value;
            int consumed;
            try
            {
                value = _runtime.ReadJsonValue(_buffer.AsSpan(_start, _count), out consumed);
            }
            catch (JqException)
            {
                if (_eof)
                    throw;
                await FillAsync().ConfigureAwait(false);
                continue;
            }
            _newlines += CountLf(_start, consumed);
            _start += consumed;
            _count -= consumed;
            SetPosition(1 + _newlines);
            return (true, value);
        }
    }

    private async Task<(bool HasValue, JsonNode? Value)> PullRawAsync()
    {
        (bool hasValue, string text, _) = await PullRawSegmentAsync().ConfigureAwait(false);
        if (!hasValue)
            return (false, null);
        return (true, JsonValue.Create(text));
    }

    // Raw segment pull for slurp aggregation: lines report whether a
    // line-feed terminator was present so slurp reconstitutes exact bytes
    // (LF-only splits, CR kept as data) instead of rejoining blindly.
    internal async Task<(bool HasValue, string Text, bool Terminated)> PullRawSegmentAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            _budget.CheckCancellation();
            if (!_active && !await ActivateNextAsync().ConfigureAwait(false))
                return (false, string.Empty, false);
            int newline = IndexOfLf();
            if (newline >= 0)
            {
                int length = newline - _start;
                ReadOnlyMemory<byte> line = _buffer.AsMemory(_start, length);
                _budget.ChargeNode();
                _budget.ChargeString(Encoding.UTF8.GetCharCount(line.Span));
                string text = Utf8Text.Decode(line);
                _start = newline + 1;
                _count -= length + 1;
                SetPosition(1 + _newlines);
                _newlines++;
                return (true, text, true);
            }
            if (_eof)
            {
                if (_count == 0)
                {
                    await CloseAndAdvanceAsync(null).ConfigureAwait(false);
                    continue;
                }
                ReadOnlyMemory<byte> tail = _buffer.AsMemory(_start, _count);
                _budget.ChargeNode();
                _budget.ChargeString(Encoding.UTF8.GetCharCount(tail.Span));
                string tailText = Utf8Text.Decode(tail);
                _start += _count;
                _count = 0;
                SetPosition(1 + _newlines);
                return (true, tailText, false);
            }
            await FillAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> ActivateNextAsync()
    {
        _budget.CheckCancellation();
        _index++;
        if (_index >= _sources.Count)
            return false;
        _start = 0;
        _count = 0;
        _eof = false;
        _newlines = 0;
        _active = true;
        LastName = _sources[_index].Name;
        Source source = _sources[_index];
        if (source.IsStdin)
        {
            _owned = null;
            return true;
        }
        JqOpenedFile opened = await JqHostExtensions.GuardHostAsync(
            () => _host.OpenReadAsync(source.Path!, _cancellationToken)).ConfigureAwait(false);
        if (opened.Error != null || opened.FileDescriptor == null)
            throw new JqException($"cannot open {source.Display}");
        _owned = opened.FileDescriptor.Value;
        return true;
    }

    private async Task FillAsync()
    {
        _budget.CheckCancellation();
        JqFileDescriptor descriptor = _owned ?? _stdin;
        // Bounded requests with a one-byte overflow probe, matching the
        // hosted read protocol pinned by the input-limit tests.
        int request = (int)Math.Min(ChunkSize, (long)_budget.RemainingInput + 1);
        JqByteReadResult read;
        try
        {
            read = await JqHostExtensions.GuardedReadAsync(
                _host, descriptor, _readBuffer.AsMemory(0, request), _cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CloseAndAdvanceAsync(exception).ConfigureAwait(false);
            throw;
        }
        _budget.CheckCancellation();
        if (read.IsError)
        {
            var failure = new JqException("input read failed");
            await CloseAndAdvanceAsync(failure).ConfigureAwait(false);
            throw failure;
        }
        if (read.BytesRead > 0)
        {
            _budget.ChargeInput(read.BytesRead);
            EnsureAdditional(read.BytesRead);
            Array.Copy(_readBuffer, 0, _buffer, _start + _count, read.BytesRead);
            _count += read.BytesRead;
        }
        if (read.ReachedEoF || read.BytesRead == 0)
            _eof = true;
    }

    private async Task CloseAndAdvanceAsync(Exception? earlier)
    {
        if (_owned is { } owned)
        {
            _owned = null;
            await _host.CloseOwnedDescriptorAsync(owned, earlier).ConfigureAwait(false);
        }
        _active = false;
        _start = 0;
        _count = 0;
        _eof = false;
    }

    private async Task AbandonCurrentAsync(Exception earlier)
    {
        try
        {
            await CloseAndAdvanceAsync(earlier).ConfigureAwait(false);
        }
        catch (Exception abandon) when (!ReferenceEquals(abandon, earlier))
        {
            if (earlier is JqException)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(earlier).Throw();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(abandon).Throw();
        }
    }

    private void SkipWhitespace()
    {
        while (_count > 0)
        {
            byte b = _buffer[_start];
            if (b != (byte)32 && b != (byte)9 && b != (byte)10 && b != (byte)13)
                break;
            if (b == (byte)10)
                _newlines++;
            _start++;
            _count--;
        }
    }

    private int IndexOfLf()
    {
        for (var i = 0; i < _count; i++)
        {
            if (_buffer[_start + i] == (byte)10)
                return _start + i;
        }
        return -1;
    }

    private int CountLf(int offset, int length)
    {
        int count = 0;
        for (var i = 0; i < length; i++)
        {
            if (_buffer[offset + i] == (byte)10)
                count++;
        }
        return count;
    }

    private void EnsureAdditional(int additional)
    {
        if (_start + _count + additional <= _buffer.Length)
            return;
        if (_count + additional <= _buffer.Length)
        {
            if (_count > 0)
                Array.Copy(_buffer, _start, _buffer, 0, _count);
            _start = 0;
            return;
        }
        int needed = _count + additional;
        int capacity = Math.Max(needed, _buffer.Length == 0 ? ChunkSize : _buffer.Length * 2);
        _budget.ChargeBytes(capacity);
        var grown = new byte[capacity];
        if (_count > 0)
            Array.Copy(_buffer, _start, grown, 0, _count);
        _buffer = grown;
        _start = 0;
    }

    private void SetPosition(int line)
    {
        string? name = _index >= 0 && _index < _sources.Count ? _sources[_index].Name : null;
        _context.InputFilename = name;
        _context.InputLineNumber = line;
        LastName = name;
        LastLine = line;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_owned is { } owned)
        {
            _owned = null;
            await _host.CloseOwnedDescriptorAsync(owned, null).ConfigureAwait(false);
        }
    }
}
