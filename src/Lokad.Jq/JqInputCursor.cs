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
// decoding retains reader state and completed DOM nodes across reads. Extended
// literals and invalid input reuse the whole-buffer reader for compatibility.
// Failures at end-of-source surface as input errors. Slurp stays an explicit
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
    private readonly List<Source> _sources;
    private readonly CancellationToken _cancellationToken;
    private readonly byte[] _readBuffer = new byte[ChunkSize];

    private int _index = -1;
    private bool _active;
    private byte[] _buffer = [];
    private int _start;
    private int _count;
    private bool _eof;
    private bool _bomPending;
    private JqFileDescriptor? _owned;
    private int _newlines;
    private bool _disposed;

    // JSON-text-sequence state (RFC 7464 records separated by RS 0x1E).
    // Raw mode wins over sequence framing; plain streaming scanners arrive
    // with the sibling `--stream` increment.
    private bool _seqWaiting = true;
    private bool _seqDone;
    private int _scanLine = 1;
    private int _scanCol;
    private readonly Queue<JsonNode?> _seqQueue = new();
    private JqSeqResyncException? _seqError;

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
        _seq = invocation.Seq;
        _stream = invocation.Stream;
        _scanner = invocation.Stream ? new JqStreamScanner(context.Budget, context.Runtime, invocation.Seq, invocation.StreamErrors) : null;
        _jsonReader = !_raw && !_seq && !_stream ? new JqRuntime.InputReader(_runtime, _budget) : null;
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
            if (_raw)
                return await PullRawAsync().ConfigureAwait(false);
            if (_stream)
                return await PullStreamAsync().ConfigureAwait(false);
            if (_seq)
                return await PullSeqAsync().ConfigureAwait(false);
            return await PullJsonAsync().ConfigureAwait(false);
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
        && exception is not JqHostFailureException
        && exception is not JqSeqResyncException;

    private async Task<(bool HasValue, JsonNode? Value)> PullJsonAsync()
    {
        while (true)
        {
            _budget.CheckCancellation();
            if (!_active && !await ActivateNextAsync().ConfigureAwait(false))
                return (false, null);
            if (_bomPending)
                await StripLeadingBomAsync().ConfigureAwait(false);
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
                if (_jsonReader is not { } reader)
                    throw new InvalidOperationException("JSON input reader is unavailable.");
                if (!reader.TryRead(_buffer.AsSpan(_start, _count), _eof, out value, out consumed))
                {
                    await FillAsync().ConfigureAwait(false);
                    continue;
                }
            }
            catch (JqException exception) when (exception is not JqQuotaException)
            {
                if (_eof)
                {
                    // Count through the failing line like the reference
                    // fgets-based total: consumed newlines plus one when the
                    // failing line is terminated, so explicit-read failures
                    // report the line being read rather than the last value.
                    int line = _newlines + (IndexOfLf() >= 0 ? 1 : 0);
                    SetPosition(line);
                    throw;
                }
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
                    continue;
                }
                ReadOnlyMemory<byte> tail = _buffer.AsMemory(_start, _count);
                _budget.ChargeNode();
                _budget.ChargeString(Encoding.UTF8.GetCharCount(tail.Span));
                string tailText = DecodeRawSegment(tail);
                _start += _count;
                _count = 0;
                SetPosition(1 + _newlines);
                return (true, tailText, false);
            }
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
        _budget.CheckCancellation();
        _index++;
        if (_index >= _sources.Count)
            return false;
        if (!_seq && !_stream)
        {
            _start = 0;
            _count = 0;
        }
        _eof = false;
        _bomPending = true;
        _newlines = 0;
        _active = true;
        LastName = _sources[_index].Name;
        // Like the reference file activation, the filename is known before
        // the first value parses, so explicit-read failures report the
        // source instead of unknown; the line stays zero until pulls advance it.
        _context.InputFilename = LastName;
        _context.InputLineNumber = 0;
        Source source = _sources[_index];
        if (source.IsStdin)
        {
            _owned = null;
            return true;
        }
        JqOpenedFile opened = await JqHostExtensions.GuardHostAsync(
            () => _host.OpenReadAsync(source.Path!, _cancellationToken)).ConfigureAwait(false);
        if (opened.Error != null || opened.FileDescriptor == null)
            throw new JqInputException($"cannot open {source.Display}", 2);
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
        _jsonReader?.Reset();
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

    // Drops one leading UTF-8 byte-order mark per source like the reference
    // document-start strip. Split marks resolve across fills; anything else
    // (including leading whitespace first) leaves bytes for normal parsing.
    private async Task StripLeadingBomAsync()
    {
        _bomPending = false;
        while (true)
        {
            int available = Math.Min(_count, 3);
            int matched = 0;
            while (matched < available && _buffer[_start + matched] == BomBytes[matched])
                matched++;
            if (matched < available)
                return;
            if (available == 3)
            {
                _start += 3;
                _count -= 3;
                return;
            }
            if (_eof)
                return;
            int before = _count;
            await FillAsync().ConfigureAwait(false);
            if (_count == before)
                return;
        }
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
    }

    private int IndexOfRs()
    {
        for (var i = 0; i < _count; i++)
        {
            if (_buffer[_start + i] == (byte)30)
                return _start + i;
        }
        return -1;
    }

    private static bool IsSeqWhitespace(byte b) =>
        b == (byte)32 || b == (byte)9 || b == (byte)10 || b == (byte)13;

    // Structural scan of one sequence record: container depth, whether the
    // record ends inside a string, and the length of a trailing literal run.
    // Literal runs are maximal trailing LITERAL-class bytes (anything but
    // whitespace, structure `[]{},:"`, or quotes).
    private static void ScanSeqRecord(
        ReadOnlySpan<byte> record, out int depth, out bool inString, out int trailingRun)
    {
        depth = 0;
        inString = false;
        trailingRun = 0;
        bool escaped = false;
        int runStart = -1;
        for (var i = 0; i < record.Length; i++)
        {
            byte b = record[i];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (b == (byte)92)
                    escaped = true;
                else if (b == (byte)34)
                {
                    inString = false;
                    runStart = -1;
                }
                continue;
            }
            if (b == (byte)34)
            {
                inString = true;
                runStart = -1;
            }
            else if (b == (byte)91 || b == (byte)123)
            {
                depth++;
                runStart = -1;
            }
            else if (b == (byte)93 || b == (byte)125)
            {
                depth--;
                runStart = -1;
            }
            else if (b == (byte)58 || b == (byte)44)
            {
                runStart = -1;
            }
            else if (IsSeqWhitespace(b))
            {
                runStart = -1;
            }
            else if (runStart < 0)
            {
                runStart = i;
            }
        }
        trailingRun = runStart < 0 ? 0 : record.Length - runStart;
    }

    private async Task<(bool HasValue, JsonNode? Value)> PullSeqAsync()
    {
        while (true)
        {
            _budget.CheckCancellation();
            if (_seqQueue.TryDequeue(out JsonNode? queued))
                return (true, queued);
            if (_seqError is not null)
            {
                JqSeqResyncException stashed = _seqError;
                _seqError = null;
                throw stashed;
            }
            if (_seqDone)
                return (false, null);
            if (!_active && !await ActivateNextAsync().ConfigureAwait(false))
            {
                FinishSeqTail();
                continue;
            }
            int rs = IndexOfRs();
            if (rs < 0)
            {
                if (_eof)
                {
                    await AdvanceSourceKeepBufferAsync().ConfigureAwait(false);
                    continue;
                }
                await FillAsync().ConfigureAwait(false);
                continue;
            }
            CompleteSeqRecord(rs);
        }
    }

    private async Task<(bool HasValue, JsonNode? Value)> PullStreamAsync()
    {
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
            if (!_active && !await ActivateNextAsync().ConfigureAwait(false))
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
                if (end.ErrorEvent is not null)
                {
                    SetPosition(1 + _newlines);
                    return (true, end.ErrorEvent);
                }
                if (end.Event is not null)
                {
                    SetPosition(1 + _newlines);
                    return (true, end.Event);
                }
                _streamEofDone = true;
                return (false, null);
            }
            if (_count == 0)
            {
                if (_eof)
                {
                    await AdvanceSourceKeepBufferAsync().ConfigureAwait(false);
                    continue;
                }
                await FillAsync().ConfigureAwait(false);
                continue;
            }
            StreamScanOutcome outcome = _scanner.Consume(_buffer.AsSpan(_start, _count), isFinal: false);
            Advance(outcome.Consumed);
            if (outcome.DropRest && _count > 0)
                Advance(_count);
            if (outcome.Error is not null)
                throw outcome.Error;
            if (outcome.ErrorEvent is not null)
            {
                SetPosition(1 + _newlines);
                return (true, outcome.ErrorEvent);
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
            await _host.CloseOwnedDescriptorAsync(owned, null).ConfigureAwait(false);
        }
        _active = false;
    }

    // Processes one RS-terminated record: values decode into the queue while
    // a truncated or malformed tail is stashed for after the drain, matching
    // reference value-before-error ordering. Only a literal run pending at the
    // separator counts as truncation; trailing whitespace finalizes scalars.
    // The span aliases the read buffer and stays valid: this path is fully
    // synchronous, so no compaction can intervene before decoding finishes.
    private void CompleteSeqRecord(int rsIdx)
    {
        int length = rsIdx - _start;
        ReadOnlySpan<byte> record = _buffer.AsSpan(_start, length);
        int recordLine = _scanLine;
        int recordCol = _scanCol + 1;
        Advance(length);
        int truncLine = _scanLine;
        int truncCol = _scanCol + 1;
        Advance(1);
        bool skippedPrefix = _seqWaiting;
        _seqWaiting = false;
        if (skippedPrefix)
            return;
        int begin = 0;
        while (begin < length && IsSeqWhitespace(record[begin]))
            begin++;
        int end = length;
        while (end > begin && IsSeqWhitespace(record[end - 1]))
            end--;
        if (begin == end)
            return;
        ScanSeqRecord(record, out int depth, out bool inString, out int trailingRun);
        if (depth > 0 || inString)
        {
            StashSeqError($"Truncated value at line {truncLine}, column {truncCol}");
            return;
        }
        if (trailingRun > 0)
        {
            DecodeSeqPrefix(record[..^trailingRun], record, recordLine, recordCol, positionErrors: true);
            if (_seqError is not null)
                return;
            ReadOnlySpan<byte> run = record[^trailingRun..];
            if (IsCompleteSeqNumber(run))
                StashSeqError($"Potentially truncated top-level numeric value at line {truncLine}, column {truncCol}");
            else
                StashSeqError($"Truncated value at line {truncLine}, column {truncCol}");
            return;
        }
        DecodeSeqValues(record, resyncSuffix: true, recordLine, recordCol);
    }

    // Decodes complete values ahead of a pending literal run. A malformed
    // prefix keeps already-queued values ahead of its own stashed error.
    // Positions a resync reader failure like the reference pending-literal
    // check: the wrapped reader reports a line offset and a byte offset
    // within that line, both relative to the decoded span. A failure inside
    // a literal run reports at the validating boundary (the run end), while
    // structural failures report at the byte itself; runs reaching the
    // record end defer to truncation. Falls back to the unpositioned message
    // when the offsets do not resolve or positioning is disabled.
    private static string FormatSeqError(ReadOnlySpan<byte> record, int spanBase, JqException error, int originLine, int originCol, bool positionErrors)
    {
        const string suffix = " (need RS to resync)";
        if (positionErrors
            && error.InnerException is System.Text.Json.JsonException json
            && json.LineNumber.HasValue
            && json.BytePositionInLine.HasValue
            && TryReaderOffset(record, spanBase, json.LineNumber.Value, json.BytePositionInLine.Value, out int failing))
        {
            int reported = failing;
            if (reported < record.Length && IsLiteralRunByte(record[reported]))
            {
                int start = reported;
                while (start > 0 && IsLiteralRunByte(record[start - 1]))
                    start--;
                int end = reported;
                while (end < record.Length && IsLiteralRunByte(record[end]))
                    end++;
                if (end >= record.Length)
                {
                    MeasurePosition(record, record.Length, originLine, originCol, out int truncLine, out int truncCol);
                    return $"Truncated value at line {truncLine}, column {truncCol}";
                }
                if (!IsValidLoneLiteral(record[start..end]))
                    reported = end;
            }
            string core = error.Message;
            string tail = $" LineNumber: {json.LineNumber.Value} | BytePositionInLine: {json.BytePositionInLine.Value}.";
            if (core.EndsWith(tail, StringComparison.Ordinal))
                core = core.Substring(0, core.Length - tail.Length);
            MeasurePosition(record, reported, originLine, originCol, out int line, out int column);
            return $"{core} at line {line}, column {column}{suffix}";
        }
        return error.Message + suffix;
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

    private void DecodeSeqPrefix(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> record, int originLine, int originCol, bool positionErrors)
    {
        int pos = 0;
        while (pos < prefix.Length)
        {
            while (pos < prefix.Length && IsSeqWhitespace(prefix[pos]))
                pos++;
            if (pos >= prefix.Length)
                return;
            JsonNode? value;
            int consumed;
            try
            {
                value = _runtime.ReadJsonValue(prefix[pos..], out consumed);
            }
            catch (JqException ex) when (ex is not JqQuotaException)
            {
                StashSeqError(FormatSeqError(record, pos, ex, originLine, originCol, positionErrors));
                return;
            }
            pos += consumed;
            _seqQueue.Enqueue(value);
        }
    }

    private void DecodeSeqValues(ReadOnlySpan<byte> stripped, bool resyncSuffix, int originLine, int originCol)
    {
        int pos = 0;
        while (pos < stripped.Length)
        {
            while (pos < stripped.Length && IsSeqWhitespace(stripped[pos]))
                pos++;
            if (pos >= stripped.Length)
                return;
            JsonNode? value;
            int consumed;
            try
            {
                value = _runtime.ReadJsonValue(stripped[pos..], out consumed);
            }
            catch (JqException ex) when (ex is not JqQuotaException)
            {
                StashSeqError(resyncSuffix ? FormatSeqError(stripped, pos, ex, originLine, originCol, positionErrors: true) : ex.Message);
                return;
            }
            pos += consumed;
            _seqQueue.Enqueue(value);
        }
    }

    // Final partial record at global end-of-input, with end-of-input message
    // variants instead of resync continuations.
    private void FinishSeqTail()
    {
        int tailLine = _scanLine;
        int tailCol = _scanCol + 1;
        int length = _count;
        ReadOnlySpan<byte> tail = _buffer.AsSpan(_start, length);
        int begin = 0;
        while (begin < length && IsSeqWhitespace(tail[begin]))
            begin++;
        int end = length;
        while (end > begin && IsSeqWhitespace(tail[end - 1]))
            end--;
        if (begin == end)
        {
            Advance(length);
            if (_seqWaiting)
            {
                _seqDone = true;
                throw new JqSeqResyncException(
                    $"Unfinished abandoned text at EOF at line {_scanLine}, column {_scanCol}");
            }
            _seqDone = true;
            return;
        }
        if (_seqWaiting)
        {
            // No record separator seen: unframed content is abandoned text
            // even when it parses as complete values, like the reference
            // waiting state (truncated strings and terms included).
            Advance(length);
            _seqDone = true;
            throw new JqSeqResyncException(
                $"Unfinished abandoned text at EOF at line {_scanLine}, column {_scanCol}");
        }
        ScanSeqRecord(tail, out int depth, out bool inString, out int trailingRun);
        if (inString)
        {
            Advance(length);
            _seqDone = true;
            StashSeqError($"Unfinished string at EOF at line {_scanLine}, column {_scanCol}");
            return;
        }
        if (depth > 0)
        {
            Advance(length);
            _seqDone = true;
            StashSeqError($"Unfinished JSON term at EOF at line {_scanLine}, column {_scanCol}");
            return;
        }
        if (trailingRun > 0)
        {
            ReadOnlySpan<byte> run = tail[^trailingRun..];
            DecodeSeqPrefix(tail[..^trailingRun], tail, tailLine, tailCol, positionErrors: false);
            if (_seqError is not null)
            {
                Advance(length);
                _seqDone = true;
                return;
            }
            if (IsCompleteSeqNumber(run))
            {
                Advance(length);
                _seqDone = true;
                StashSeqError($"Potentially truncated top-level numeric value at EOF at line {_scanLine}, column {_scanCol}");
                return;
            }
            if (IsCompleteSeqLiteral(run))
            {
                JsonNode? literal = _runtime.ReadJsonValue(run, out _);
                Advance(length);
                _seqDone = true;
                _seqQueue.Enqueue(literal);
                return;
            }
            Advance(length);
            _seqDone = true;
            char first = (char)run[0];
            bool numericStart = char.IsAsciiDigit(first) || first is '-' or '.';
            StashSeqError(numericStart
                ? $"Invalid numeric literal at EOF at line {_scanLine}, column {_scanCol}"
                : $"Invalid literal at EOF at line {_scanLine}, column {_scanCol}");
            return;
        }
        DecodeSeqValues(tail, resyncSuffix: false, tailLine, tailCol);
        Advance(length);
        _seqDone = true;
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
                MaxDepth = JqBudget.MaximumDepth
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

    private void StashSeqError(string message)
    {
        _budget.CheckCancellation();
        _budget.ChargeString(message.Length);
        _seqError = new JqSeqResyncException(message);
        SetPosition(1 + _newlines);
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
        if (_owned is { } owned)
        {
            _owned = null;
            await _host.CloseOwnedDescriptorAsync(owned, earlier).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAbandonedAsync(null).ConfigureAwait(false);
    }
}
