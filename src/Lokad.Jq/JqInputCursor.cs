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
// Sources resolve on demand: no file operands means one stdin source,
// `-` operands reuse the borrowed stdin descriptor at its current position
// and are never closed, and every other operand opens lazily on first
// demand so early termination never touches later files. File descriptors
// obtained here are owned until the source is exhausted, the pull fails,
// or the cursor is disposed; stdin is always borrowed.
//
// Reads are chunked (8 KiB): JSON values and raw lines may span chunk
// boundaries; shared admission preflights staging and scalar storage. JSON
// decoding retains reader state and completed DOM nodes across reads. Extended
// literals share the token reader; malformed input keeps its diagnostics.
// Logical EOF finalizes JSON; source transitions retain partial values. Slurp stays an explicit
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
    private readonly bool _seq;
    private readonly bool _stream;
    private readonly JqStreamScanner? _scanner;
    private readonly JqRuntime.InputReader? _jsonReader;
    private bool _streamEofDone;
    private readonly IReadOnlyList<string> _sources;
    private readonly JqPath _workingDirectory;
    private readonly CancellationToken _cancellationToken;
    private byte[] _readBuffer = [];
    private readonly JqMemoryReservation _readMemory;
    private readonly JqMemoryReservation _bufferMemory;
    private readonly JqStorage? _retainedValues;

    private int _index = -1;
    private bool _active;
    private byte[] _buffer = [];
    private int _start;
    private int _count;
    private bool _eof;
    private JqOperationError? _readError;
    private int _bomPosition;
    private bool _reportedFailure;

    internal void RecordFailure() => _reportedFailure = true;
    internal bool HasOpenFailures { get; private set; }
    private JqFileDescriptor? _owned;
    private int _newlines;
    private bool _disposed;

    // JSON-text-sequence state (RFC 7464 records separated by RS 0x1E).
    // Raw mode wins over sequence framing.
    private bool _seqWaiting = true;
    private bool _seqDone;
    private int _scanLine = 1;
    private int _scanCol;
    private bool _inputEof;
    private int _seqSearched;
    private int _seqBoundary = -1;

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
        _readMemory = new JqMemoryReservation(_budget.MemoryBudget);
        _bufferMemory = new JqMemoryReservation(_budget.MemoryBudget);
        _runtime = context.Runtime;
        _context = context;
        _stdin = invocation.StdIn;
        _raw = invocation.RawInput;
        _seq = !_raw && invocation.Seq;
        _stream = !_raw && invocation.Stream;
        _retainedValues = _stream ? _budget.CreateStorageOwner() : null;
        _scanner = _stream ? new JqStreamScanner(context.Budget, context.Runtime, invocation.Seq, invocation.StreamErrors) : null;
        _jsonReader = !_raw && !_stream ? new JqRuntime.InputReader(_runtime, _budget) : null;
        _cancellationToken = cancellationToken;
        _sources = invocation.InputFiles;
        _workingDirectory = invocation.WorkingDirectory;
    }

    private string? CurrentName => _index < 0 || _index >= Math.Max(1, _sources.Count) ? null
        : _sources.Count == 0 || _sources[_index] == "-" ? "<stdin>" : _sources[_index];

    internal string? LastName { get; private set; }

    internal int LastLine { get; private set; }

    internal void ReleaseCompletedRecordStorage()
    {
        _retainedValues?.ReconcileCursor(_scanner?.RetainedValueBytes ?? 0);
    }

    // Async pull for outer iteration. Returns false when every source is
    // exhausted; otherwise sets filename/line state and returns the value.
    internal async Task<(bool HasValue, JsonNode? Value)> PullAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (_raw)
                return await PullRawAsync().ConfigureAwait(false);
            if (_bomPosition < BomBytes.Length)
                await StripLeadingBomAsync().ConfigureAwait(false);
            if (_stream)
                return await PullStreamAsync().ConfigureAwait(false);
            if (_seq)
                return await PullSeqAsync().ConfigureAwait(false);
            return await PullJsonAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (ShouldAbandonSource(exception))
        {
            await CloseAndAdvanceAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private static bool ShouldAbandonSource(Exception exception) =>
        exception is not OperationCanceledException
        && exception is not JqQuotaException
        && exception is not JqHostFailureException
        && exception is not JqSeqResyncException;

    private async Task<(bool HasValue, JsonNode? Value)> PullJsonAsync()
    {
        while (true)
        {
            _budget.CheckCancellation();
            SkipWhitespace();
            if (_count == 0)
            {
                if (!await ReadMoreAsync().ConfigureAwait(false)) return (false, null);
                continue;
            }
            JsonNode? value;
            int consumed;
            int valueStartLine = _newlines;
            bool valueHasLineFeed = false;
            try
            {
                if (_jsonReader is not { } reader)
                    throw new InvalidOperationException("JSON input reader is unavailable.");
                while (!reader.TryRead(_buffer.AsSpan(_start, _count),
                    _inputEof || _eof && _index == Math.Max(1, _sources.Count) - 1, out value, out consumed))
                {
                    int newlines = CountLf(_start, consumed);
                    valueHasLineFeed |= newlines != 0;
                    _newlines += newlines;
                    _start += consumed;
                    _count -= consumed;
                    int previousSource = _index;
                    await ReadMoreAsync().ConfigureAwait(false);
                    if (_index != previousSource)
                    {
                        valueStartLine = _newlines;
                        valueHasLineFeed = false;
                    }
                }
            }
            catch (JqException exception) when (exception is not JqQuotaException)
            {
                // Explicit reads report the failing line already present in
                // the buffer; known-invalid input never needs another host read.
                int line = valueStartLine + (valueHasLineFeed || IndexOfLf() >= 0 ? 1 : 0);
                SetPosition(line);
                throw;
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
        int scanned = 0;
        while (true)
        {
            _budget.CheckCancellation();
            if (!_active && !await ActivateNextAsync().ConfigureAwait(false))
                return (false, string.Empty, false);
            int relativeNewline = _buffer.AsSpan(_start + scanned, _count - scanned).IndexOf((byte)'\n');
            int newline = relativeNewline < 0 ? -1 : _start + scanned + relativeNewline;
            if (newline >= 0)
            {
                int length = newline - _start;
                ReadOnlyMemory<byte> line = _buffer.AsMemory(_start, length);
                _budget.CheckCancellation();
                _budget.ReserveString(Encoding.UTF8.GetCharCount(line.Span));
                string text = DecodeRawSegment(line);
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
                    scanned = 0;
                    continue;
                }
                ReadOnlyMemory<byte> tail = _buffer.AsMemory(_start, _count);
                _budget.CheckCancellation();
                _budget.ReserveString(Encoding.UTF8.GetCharCount(tail.Span));
                string tailText = DecodeRawSegment(tail);
                _start += _count;
                _count = 0;
                SetPosition(1 + _newlines);
                return (true, tailText, false);
            }
            scanned = _count;
            await FillAsync().ConfigureAwait(false);
        }
    }

    // Invalid bytes are malformed input (staged exit 5 like invalid JSON),
    // never a bare decoder failure leaking the stage number.
    private static string DecodeRawSegment(ReadOnlyMemory<byte> segment)
    {
        try
        {
            return Utf8Text.Decode(segment);
        }
        catch (DecoderFallbackException)
        {
            throw new JqInputException("parse error: Cannot transcode invalid UTF-8 raw input to UTF-16 string.", 5);
        }
    }

    private async Task<bool> ActivateNextAsync()
    {
        while (true)
        {
            _budget.CheckCancellation();
            _index++;
            if (_index >= Math.Max(1, _sources.Count))
                return false;
            if (_raw)
            {
                _start = 0;
                _count = 0;
            }
            _eof = false;
            _newlines = 0;
            _active = true;
            LastName = CurrentName;
            // Like the reference file activation, the filename is known before
            // the first value parses, so explicit-read failures report the
            // source instead of unknown; the line stays zero until pulls advance it.
            _context.InputFilename = LastName;
            _context.InputLineNumber = 0;
            if (_sources.Count == 0 || _sources[_index] == "-")
            {
                _owned = null;
                return true;
            }
            string operand = _sources[_index];
            using var pathMemory = new JqStorage(_budget.MemoryBudget);
            JqResolvedPath source;
            using (_budget.UseStorage(pathMemory))
            {
                try { source = JqPathResolution.ResolveArgument(operand, _workingDirectory, _budget); }
                catch (Exception exception) when (exception is FormatException or JqPathException)
                { throw new JqInputException(exception.Message, 2); }
            }
            JqOpenedFile opened = await JqHostExtensions.GuardedOpenAsync(
                _host, source.Absolute, _cancellationToken).ConfigureAwait(false);
            if (opened.FileDescriptor is not { } descriptor)
            {
                HasOpenFailures = true;
                RecordFailure();
                pathMemory.Dispose();
                var (host, stderr) = _context.Stderr
                    ?? throw new InvalidOperationException("Input diagnostics require a host descriptor.");
                JqAppendResult? written = await JqDiagnosticWriter.TryWriteAsync(host, stderr,
                    ["jq: cannot open ", source.Display, ": ", opened.Error?.Message
                        ?? throw new InvalidOperationException("Missing open error.")],
                    true, _budget.MemoryBudget, _cancellationToken).ConfigureAwait(false);
                if (written is not { CanAcceptMore: true })
                    throw new JqOutputClosedException(written is null ? 5 : 2);
                _active = false;
                continue;
            }
            _owned = descriptor;
            return true;
        }
    }

    private async Task FillAsync()
    {
        _budget.CheckCancellation();
        if (_readError is { } pending)
            throw new JqOperationException("reading", CurrentName ?? "<unknown>", pending);
        if (_readBuffer.Length == 0)
            _readMemory.ReplaceBuffer(ref _readBuffer, ChunkSize, _cancellationToken);
        JqFileDescriptor descriptor = _owned ?? _stdin;
        JqByteReadResult read;
        try
        {
            read = await JqHostExtensions.GuardedReadAsync(
                _host, descriptor, _readBuffer, _cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CloseAndAdvanceAsync(exception).ConfigureAwait(false);
            throw;
        }
        _budget.CheckCancellation();
        if (read.BytesRead > 0)
        {
            EnsureAdditional(read.BytesRead);
            Array.Copy(_readBuffer, 0, _buffer, _start + _count, read.BytesRead);
            _count += read.BytesRead;
        }
        _readError = read.Error;
        if (_readError is { } error && read.BytesRead == 0)
            throw new JqOperationException("reading", CurrentName ?? "<unknown>", error);
        if (read.ReachedEoF || read.BytesRead == 0)
            _eof = true;
    }

    private async Task CloseAndAdvanceAsync(Exception? earlier)
    {
        _jsonReader?.Reset();
        _seqSearched = 0;
        _seqBoundary = -1;
        if (_owned is { } owned)
        {
            _owned = null;
            await _host.CloseOwnedDescriptorAsync(owned, CurrentName ?? "<unknown>", earlier is not null || _reportedFailure).ConfigureAwait(false);
        }
        _active = false;
        _start = 0;
        _count = 0;
        _eof = false;
        _readError = null;
    }

    // The command input is one JSON stream, including across physical files.
    // Consume its initial BOM incrementally without adding diagnostic columns.
    private async Task StripLeadingBomAsync()
    {
        while (_bomPosition < BomBytes.Length)
        {
            _budget.CheckCancellation();
            if (_count == 0)
            {
                if (await ReadMoreAsync().ConfigureAwait(false)) continue;
                _bomPosition = BomBytes.Length;
                return;
            }
            if (_buffer[_start] != BomBytes[_bomPosition])
            {
                bool malformed = _bomPosition != 0;
                _bomPosition = BomBytes.Length;
                if (malformed && !_seq) throw new JqException("Malformed BOM");
                return;
            }
            _start++;
            _count--;
            _bomPosition++;
        }
    }

    private async Task<bool> ReadMoreAsync()
    {
        while (!_inputEof)
        {
            if (_active && !_eof)
            {
                await FillAsync().ConfigureAwait(false);
                return true;
            }
            if (_active) await AdvanceSourceKeepBufferAsync().ConfigureAwait(false);
            if (!await ActivateNextAsync().ConfigureAwait(false)) _inputEof = true;
        }
        return false;
    }

    private static readonly byte[] BomBytes = [0xEF, 0xBB, 0xBF];

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

    // Advances past length bytes, counting newlines for per-source input
    // metadata and continuous scanner diagnostics alike.
    private void Advance(int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (_buffer[_start + i] == (byte)10)
            {
                _newlines++;
                _scanLine++;
                _scanCol = 0;
            }
            else
            {
                _scanCol++;
            }
        }
        _start += length;
        _count -= length;
        _seqSearched = Math.Max(0, _seqSearched - length);
        _seqBoundary = _seqBoundary < length ? -1 : _seqBoundary - length;
    }

    private static bool IsSeqWhitespace(byte b) =>
        b == (byte)32 || b == (byte)9 || b == (byte)10 || b == (byte)13;

    private async Task<(bool HasValue, JsonNode? Value)> PullSeqAsync()
    {
        // A pull finishes or abandons its tree before yielding. Unlike stream
        // events, sequence values need no owner beyond the current outer record.
        var storage = _budget.TemporaryStorage
            ?? throw new InvalidOperationException("Sequence input requires record storage.");
        long pendingBytes = 0;
        bool started = false;
        int originLine = 0;
        int originColumn = 0;
        if (_jsonReader is not { } reader)
            throw new InvalidOperationException("JSON input reader is unavailable.");
        while (true)
        {
            _budget.CheckCancellation();
            if (_seqDone) return (false, null);
            int length = SectionLength();
            if (_seqWaiting)
            {
                Advance(length);
                if (_seqBoundary == 0)
                {
                    Advance(1);
                    _seqWaiting = false;
                    continue;
                }
                if (await ReadMoreAsync().ConfigureAwait(false)) continue;
                _seqDone = true;
                _budget.ReserveString(160);
                throw Warning($"Unfinished abandoned text at EOF at line {_scanLine}, column {_scanCol}");
            }
            if (!started)
            {
                int whitespace = 0;
                while (whitespace < length && IsSeqWhitespace(_buffer[_start + whitespace])) whitespace++;
                Advance(whitespace);
                length -= whitespace;
                if (length == 0)
                {
                    if (_seqBoundary == 0) { Advance(1); continue; }
                    if (await ReadMoreAsync().ConfigureAwait(false)) continue;
                    _seqDone = true;
                    return (false, null);
                }
                originLine = _scanLine;
                originColumn = _scanCol + 1;
                started = true;
            }
            long before = storage.UsedBytes;
            try
            {
                // Sequence literals need a following delimiter. The standard
                // reader already waits for numbers; defer bare booleans/null too.
                bool bareLiteral = reader.ContainerDepth == 0
                    && IsCompleteSeqLiteral(_buffer.AsSpan(_start, length));
                int consumed = 0;
                if ((!bareLiteral || _inputEof) && reader.TryRead(_buffer.AsSpan(_start, length),
                    _inputEof && bareLiteral, out JsonNode? value, out consumed))
                {
                    Advance(consumed);
                    SetPosition(1 + _newlines);
                    return (true, value);
                }
                pendingBytes += storage.UsedBytes - before;
                // Retire committed tokens, keeping the unfinished token bytes.
                Advance(consumed);
            }
            catch (JqException error) when (error is not JqQuotaException)
            {
                pendingBytes += storage.UsedBytes - before;
                throw await ParseWarningAsync(error, reader.ContainerDepth, originLine, originColumn).ConfigureAwait(false);
            }
            length = SectionLength();
            if (_seqBoundary >= 0 || _inputEof)
            {
                bool inString = length != 0 && _buffer[_start] == (byte)'"';
                bool number = reader.ContainerDepth == 0
                    && IsCompleteSeqNumber(_buffer.AsSpan(_start, length));
                bool numericStart = length != 0 && (_buffer[_start] is >= (byte)'0' and <= (byte)'9'
                    or (byte)'-' or (byte)'.');
                string reason = inString ? "Unfinished string" : reader.ContainerDepth != 0
                    ? "Unfinished JSON term" : number ? "Potentially truncated top-level numeric value"
                    : numericStart ? "Invalid numeric literal" : "Invalid literal";
                throw BoundaryWarning(length, reason, number);
            }
            await ReadMoreAsync().ConfigureAwait(false);
        }

        int SectionLength()
        {
            if (_seqBoundary < 0)
            {
                int found = _buffer.AsSpan(_start + _seqSearched, _count - _seqSearched).IndexOf((byte)30);
                if (found >= 0) _seqBoundary = _seqSearched + found;
                else _seqSearched = _count;
            }
            return _seqBoundary < 0 ? _count : _seqBoundary;
        }

        JqSeqResyncException Warning(string message)
        {
            SetPosition(1 + _newlines);
            return new JqSeqResyncException(message);
        }

        void AbandonValue()
        {
            reader.Reset();
            storage.DiscardBytes(pendingBytes);
        }

        JqSeqResyncException BoundaryWarning(int length, string eofReason, bool number)
        {
            Advance(length);
            bool separator = _seqBoundary == 0;
            int line = _scanLine;
            int column = _scanCol + (separator ? 1 : 0);
            if (separator) Advance(1);
            else _seqDone = true;
            AbandonValue();
            _budget.ReserveString(160);
            return Warning(separator
                ? $"{(number ? "Potentially truncated top-level numeric value" : "Truncated value")} at line {line}, column {column}"
                : $"{eofReason} at EOF at line {line}, column {column}");
        }

        async Task<JqSeqResyncException> ParseWarningAsync(JqException error, int depth, int valueLine, int valueColumn)
        {
            _budget.ReserveString(2L * error.Message.Length + 256);
            int failing = 0;
            string core = error.Message;
            if (error.InnerException is System.Text.Json.JsonException json
                && json.LineNumber is long relativeLine && json.BytePositionInLine is long relativeColumn)
            {
                long line = valueLine + relativeLine;
                long column = relativeColumn + (relativeLine == 0 ? valueColumn : 1);
                TryReaderOffset(_buffer.AsSpan(_start, SectionLength()), 0, line - _scanLine,
                    column - (line == _scanLine ? _scanCol + 1 : 1), out failing);
                if (failing < 0) failing = 0;
                string tail = $" LineNumber: {relativeLine} | BytePositionInLine: {relativeColumn}.";
                if (core.EndsWith(tail, StringComparison.Ordinal)) core = core[..^tail.Length];
            }
            int length = SectionLength();
            bool literal = failing < length && IsLiteralRunByte(_buffer[_start + failing]);
            bool quoted = failing < length && _buffer[_start + failing] == (byte)'"';
            if (literal)
            {
                int begin = failing;
                while (begin > 0 && IsLiteralRunByte(_buffer[_start + begin - 1])) begin--;
                int end = failing;
                while (end < length && IsLiteralRunByte(_buffer[_start + end])) end++;
                if (end < length && IsValidLoneLiteral(_buffer.AsSpan(_start + begin, end - begin)))
                    literal = false;
            }
            int reportLine = _scanLine;
            int reportColumn = _scanCol + 1;
            MeasurePosition(_buffer.AsSpan(_start, length), failing, reportLine, reportColumn,
                out reportLine, out reportColumn);
            if (literal || quoted)
            {
                Advance(failing);
                bool opening = quoted;
                bool escaped = false;
                while (true)
                {
                    length = SectionLength();
                    int end = 0;
                    bool finished = false;
                    while (end < length)
                    {
                        byte current = _buffer[_start + end];
                        if (literal && !IsLiteralRunByte(current)) { finished = true; break; }
                        end++;
                        if (quoted)
                        {
                            if (opening) opening = false;
                            else if (escaped) escaped = false;
                            else if (current == 92) escaped = true;
                            else if (current == (byte)'"') { finished = true; break; }
                        }
                    }
                    Advance(end);
                    if (finished)
                    {
                        if (literal) { reportLine = _scanLine; reportColumn = _scanCol + 1; }
                        break;
                    }
                    if (_seqBoundary == 0 || _inputEof)
                        return BoundaryWarning(0, quoted ? "Unfinished string" : depth != 0
                            ? "Unfinished JSON term" : "Invalid literal", false);
                    await ReadMoreAsync().ConfigureAwait(false);
                }
            }
            AbandonValue();
            _seqWaiting = true;
            return Warning($"{core} at line {reportLine}, column {reportColumn} (need RS to resync)");
        }
    }

    private async Task<(bool HasValue, JsonNode? Value)> PullStreamAsync()
    {
        ArgumentNullException.ThrowIfNull(_retainedValues);
        using var storage = _budget.UseStorage(_retainedValues);
        ArgumentNullException.ThrowIfNull(_scanner);
        while (true)
        {
            _budget.CheckCancellation();
            if (_scanner.TryDrainStash(out JsonNode? stashed))
            {
                SetPosition(1 + _newlines);
                return (true, stashed);
            }
            if (_streamEofDone)
                return (false, null);
            if (_count == 0 && !await ReadMoreAsync().ConfigureAwait(false))
            {
                StreamScanOutcome end;
                try
                {
                    end = _scanner.FinishFinal();
                }
                catch (JqException)
                {
                    _streamEofDone = true;
                    throw;
                }
                if (end.Event is not null)
                {
                    SetPosition(1 + _newlines);
                    return (true, end.Event);
                }
                _streamEofDone = true;
                return (false, null);
            }
            if (_count == 0) continue;
            StreamScanOutcome outcome = _scanner.Consume(_buffer.AsSpan(_start, _count));
            Advance(outcome.Consumed);
            if (outcome.DropRest && _count > 0)
                Advance(_count);
            if (outcome.Error is { } error)
            {
                SetPosition(1 + _newlines);
                throw error;
            }
            if (outcome.Event is not null)
            {
                SetPosition(1 + _newlines);
                return (true, outcome.Event);
            }
        }
    }

    private async Task AdvanceSourceKeepBufferAsync()
    {
        if (_owned is { } owned)
        {
            _owned = null;
            await _host.CloseOwnedDescriptorAsync(owned, CurrentName ?? "<unknown>", _reportedFailure).ConfigureAwait(false);
        }
        _active = false;
    }

    private static bool TryReaderOffset(ReadOnlySpan<byte> record, int spanBase, long errorLine, long errorColumn, out int failing)
    {
        failing = -1;
        if (errorLine < 0 || errorColumn < 0 || spanBase < 0 || spanBase > record.Length)
            return false;
        int offset = spanBase;
        long skipped = errorLine;
        while (offset < record.Length && skipped > 0)
        {
            if (record[offset] == (byte)10)
                skipped--;
            offset++;
        }
        if (skipped != 0)
            return false;
        long target = (long)offset + errorColumn;
        if (target < 0 || target > record.Length)
            return false;
        failing = (int)target;
        return true;
    }

    private static void MeasurePosition(ReadOnlySpan<byte> span, long length, int originLine, int originCol, out int line, out int column)
    {
        line = originLine;
        column = originCol;
        for (long i = 0; i < length; i++)
        {
            if (span[(int)i] == (byte)10)
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
    }

    private static bool IsLiteralRunByte(byte b) =>
        (b >= (byte)97 && b <= (byte)122) || (b >= (byte)65 && b <= (byte)90) || (b >= (byte)48 && b <= (byte)57) || b == (byte)46 || b == (byte)43 || b == (byte)45;

    private static bool IsValidLoneLiteral(ReadOnlySpan<byte> run)
    {
        if (run.SequenceEqual("true"u8) || run.SequenceEqual("false"u8) || run.SequenceEqual("null"u8))
            return true;
        string text = System.Text.Encoding.UTF8.GetString(run);
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && !double.IsNaN(value)
            && !double.IsInfinity(value);
    }

    private static bool IsCompleteSeqNumber(ReadOnlySpan<byte> run)
    {
        if (run.IsEmpty)
            return false;
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(run, new System.Text.Json.JsonReaderOptions
            {
                AllowTrailingCommas = true,
                AllowMultipleValues = true,
                MaxDepth = JqRecursion.MaximumDepth
            });
            if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.Number)
                return false;
            return (long)reader.BytesConsumed == run.Length;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsCompleteSeqLiteral(ReadOnlySpan<byte> run) =>
        run.SequenceEqual("true"u8) || run.SequenceEqual("false"u8) || run.SequenceEqual("null"u8);

    private void EnsureAdditional(int additional)
    {
        if ((long)_start + _count + additional <= _buffer.Length)
            return;
        if ((long)_count + additional <= _buffer.Length)
        {
            if (_count > 0)
                Array.Copy(_buffer, _start, _buffer, 0, _count);
            _start = 0;
            return;
        }
        long needed = (long)_count + additional;
        int capacity = JqBudget.BufferCapacity(needed, _buffer.Length == 0 ? ChunkSize : 2L * _buffer.Length);
        _budget.CheckCancellation();
        _bufferMemory.ReplaceBuffer(ref _buffer, capacity, _start, _count, _cancellationToken);
        _start = 0;
    }

    private void SetPosition(int line)
    {
        string? name = CurrentName;
        _context.InputFilename = name;
        _context.InputLineNumber = line;
        LastName = name;
        LastLine = line;
    }

    // Closes an abandoned owned descriptor with the in-flight failure as
    // context, so persistent cleanup failures cannot replace quota,
    // cancellation, host, or language errors unwinding through the executor.
    // A null failure keeps the existing lone-cleanup-failure report.
    internal async ValueTask CloseAbandonedAsync(Exception? earlier)
    {
        if (_disposed)
            return;
        _disposed = true;
        _jsonReader?.Reset();
        var pending = earlier is null && !_reportedFailure && _readError is { } error
            ? new JqOperationException("reading", CurrentName ?? "<unknown>", error) : null;
        try
        {
            if (_owned is { } owned)
            {
                _owned = null;
                await _host.CloseOwnedDescriptorAsync(owned, CurrentName ?? "<unknown>",
                    earlier is not null || _reportedFailure || pending is not null).ConfigureAwait(false);
            }
            if (pending is not null) throw pending;
        }
        finally
        {
            _scanner?.Dispose();
            _readBuffer = [];
            _buffer = [];
            _readMemory.Dispose();
            _bufferMemory.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAbandonedAsync(null).ConfigureAwait(false);
    }
}
