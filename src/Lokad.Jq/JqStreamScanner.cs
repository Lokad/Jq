using System;
using System.Collections.Generic;

namespace Lokad.Jq;

// Outcome of one streaming scan step: bytes to advance past plus at most
// one event or recoverable warning. The cursor retires consumed bytes before
// raising a warning; terminal failures still propagate immediately.
internal readonly record struct StreamScanOutcome(
    int Consumed,
    JqValue? Event,
    JqSeqResyncException? Error,
    bool DropRest)
{
    public static StreamScanOutcome NeedsMore(int consumed) =>
        new(consumed, null, null, false);

    public static StreamScanOutcome WithEvent(int consumed, JqValue? @event) =>
        new(consumed, @event, null, false);

    public static StreamScanOutcome WithEvent(int consumed, JqValue? @event, bool dropRest) =>
        new(consumed, @event, null, dropRest);

    public static StreamScanOutcome WithWarning(int consumed, string message) =>
        new(consumed, null, new JqSeqResyncException(message), false);

    public static StreamScanOutcome Finished() =>
        new(0, null, null, false);
}

// Incremental byte-level scanner producing `jq --stream` events without a
// document DOM: only the path stack (depth-bounded), the pending scalar
// token, and one stashed close event are retained. Structure, separator
// rules, and diagnostic shapes mirror the reference streaming parser;
// scalar decoding reuses the whole-value reader so numbers, strings, and
// escapes match normal input mode.
//
// Event forms: `[path, leaf]` for scalars and empty containers, `[path]`
// for completed containers. With `--stream-errors`, recoverable failures
// surface as `["message", path]` values and scanning continues; with
// `--seq`, records split at RS with truncation checks and resync recovery.
internal sealed class JqStreamScanner : IDisposable
{
    private readonly JqBudget _budget;
    private readonly JqRuntime _runtime;
    private readonly JqMemoryReservation _tokenMemory;
    private readonly bool _seq;
    private readonly bool _streamErrors;

    private enum LastSeen { None, OpenArray, OpenObject, Colon, Comma, Value }

    private readonly struct Segment
    {
        public readonly bool IsIndex;
        public readonly long Index;
        public readonly JqString? Key;

        private Segment(bool isIndex, long index, JqString? key)
        {
            IsIndex = isIndex;
            Index = index;
            Key = key;
        }

        public static Segment Pending() => new(false, 0, null);

        public static Segment AtIndex(long index) => new(true, index, null);

        public static Segment AtKey(JqString key) => new(false, 0, key);

        public bool IsPending => !IsIndex && Key is null;
    }

    private readonly List<Segment> _path = new();
    private LastSeen _lastSeen;
    private JqValue? _next;
    private bool _hasNext;
    private JqValue? _stashed;
    private long _stashedBytes;
    private long _pathKeyBytes;
    private byte[] _token = [];
    private int _tokenLength;
    private bool _inString;
    private JqStringToken _stringToken;
    private bool _lastWasWs = true;
    private bool _seqWaiting;
    private bool _eofConcluded;
    private int _line = 1;
    private int _column;

    internal JqStreamScanner(JqBudget budget, JqRuntime runtime, bool seq, bool streamErrors)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(runtime);
        _budget = budget;
        _runtime = runtime;
        _tokenMemory = new JqMemoryReservation(budget.MemoryBudget);
        _seq = seq;
        _streamErrors = streamErrors;
        _seqWaiting = seq;
    }

    internal bool TryDrainStash(out JqValue? @event)
    {
        if (_stashed is not null)
        {
            @event = _stashed;
            _stashed = null;
            return true;
        }
        @event = null;
        return false;
    }

    // Pending leaves are scalars; close events cache their known flat path
    // size while copying it. No retained DOM requires an accounting walk.
    internal long RetainedValueBytes =>
        (_next is null ? 0 : _next is JqString text ? 80L + text.ByteLength : 64)
        + (_stashed is null ? 0 : _stashedBytes) + _pathKeyBytes;

    public void Dispose()
    {
        _token = [];
        _next = null;
        _stashed = null;
        _path.Clear();
        _pathKeyBytes = 0;
        _tokenMemory.Dispose();
    }

    // Scans bytes until the first event, warning, or stall. The cursor drains
    // close events before calling and finalizes EOF separately.
    internal StreamScanOutcome Consume(ReadOnlySpan<byte> input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            int previousLine = _line;
            int previousColumn = _column;
            byte b = input[i];
            if (b == (byte)10)
            {
                _line++;
                _column = 0;
            }
            else
            {
                _column++;
            }
            if (_seq && _seqWaiting)
            {
                if (b == (byte)30)
                    _seqWaiting = false;
                continue;
            }
            if (_seq && b == (byte)30)
                return EndStreamRecord(i + 1);
            _lastWasWs = false;
            if (_inString)
            {
                AppendToken(b);
                _stringToken.Advance(b, out bool closed);
                if (closed)
                    return FinishString(i + 1);
                continue;
            }
            bool whitespace = IsStreamWhitespace(b);
            bool structural = b is (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)':' or (byte)',';
            if (whitespace) _lastWasWs = true;
            if (_tokenLength > 0 && (whitespace || structural || b == (byte)'"'))
            {
                StreamScanOutcome? literal = FinishLiteral(i);
                if (literal is { } outcome)
                {
                    // The delimiter remains for the next pull. Its diagnostic
                    // position is visible above, but it must advance only once.
                    _line = previousLine;
                    _column = previousColumn;
                    return outcome;
                }
            }
            if (whitespace) continue;
            if (b == (byte)34)
            {
                _inString = true;
                _stringToken = default;
                AppendToken(b);
                continue;
            }
            if (structural)
                return Structure((char)b, i + 1);
            AppendToken(b);
        }
        return StreamScanOutcome.NeedsMore(input.Length);
    }

    private static bool IsStreamWhitespace(byte b) =>
        b == (byte)32 || b == (byte)9 || b == (byte)10 || b == (byte)13;

    // A literal token completed at a delimiter: decode it, bind it as the
    // pending value, and report a top-level scalar event when ready. Null
    // means scanning continues after the delimiter.
    private StreamScanOutcome? FinishLiteral(int consumed)
    {
        if (!TryDecodeToken(out JqValue? scalar))
            return Blame(consumed, TokenStartsWithLiteral(0) ? "Invalid literal" : "Invalid numeric literal");
        _tokenLength = 0;
        string? separator = BindValue(scalar);
        if (separator is not null)
            return Blame(consumed, separator);
        JqValue? emitted = CheckDone();
        return emitted is null
            ? null
            : StreamScanOutcome.WithEvent(consumed, emitted);
    }

    private StreamScanOutcome FinishString(int consumed)
    {
        JqValue? scalar;
        try
        {
            scalar = _runtime.ReadJsonValue(_token.AsSpan(0, _tokenLength), out int used);
            if (used != _tokenLength || scalar is not JqScalar)
                return Blame(consumed, "Invalid string");
        }
        catch (JqException ex) when (ex is not JqQuotaException)
        {
            _tokenLength = 0;
            _inString = false;
            return Blame(consumed, ex.Message);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            _tokenLength = 0;
            _inString = false;
            return Blame(consumed, "Invalid string");
        }
        _tokenLength = 0;
        _inString = false;
        string? separator = BindValue(scalar);
        if (separator is not null)
            return Blame(consumed, separator);
        JqValue? emitted = CheckDone();
        return emitted is null
            ? StreamScanOutcome.NeedsMore(consumed)
            : StreamScanOutcome.WithEvent(consumed, emitted);
    }

    // Binds a decoded scalar as the pending value. Returns a separator
    // diagnostic when another value is already pending.
    private string? BindValue(JqValue? scalar)
    {
        if (_hasNext || _lastSeen == LastSeen.Value)
            return "Expected separator between values";
        _next = scalar;
        _hasNext = true;
        _lastSeen = _path.Count > 0 ? LastSeen.Value : LastSeen.None;
        return null;
    }

    // Emits a completed top-level scalar, if any is pending.
    private JqValue? CheckDone()
    {
        if (_path.Count == 0 && _hasNext)
        {
            JqValue? leaf = _next;
            _next = null;
            _hasNext = false;
            return LeafEvent(new JqArray(), leaf);
        }
        return null;
    }

    private StreamScanOutcome Structure(char op, int consumed)
    {
        switch (op)
        {
            case '[':
                if (_hasNext)
                    return Blame(consumed, "Expected a separator between values");
                if (_lastSeen == LastSeen.OpenObject)
                    return Blame(consumed, "Expected string key after '{', not '['");
                if (_lastSeen == LastSeen.Comma && !_path[^1].IsIndex)
                    return Blame(consumed, "Expected string key after ',' in object, not '['");
                PushIndex(0);
                _lastSeen = LastSeen.OpenArray;
                return StreamScanOutcome.NeedsMore(consumed);
            case '{':
                if (_lastSeen == LastSeen.Value)
                    return Blame(consumed, "Expected a separator between values");
                if (_lastSeen == LastSeen.OpenObject)
                    return Blame(consumed, "Expected string key after '{', not '{'");
                if (_lastSeen == LastSeen.Comma && !_path[^1].IsIndex)
                    return Blame(consumed, "Expected string key after ',' in object, not '{'");
                PushPending();
                _lastSeen = LastSeen.OpenObject;
                return StreamScanOutcome.NeedsMore(consumed);
            case ':':
                if (_path.Count == 0 || _path[^1].IsIndex)
                    return Blame(consumed, "':' not as part of an object");
                if (!_hasNext)
                    return Blame(consumed, "Expected string key before ':'");
                if (_next is not JqString name)
                    return Blame(consumed, "Object keys must be strings");
                if (_lastSeen != LastSeen.Value)
                    return Blame(consumed, "':' should follow a key");
                _pathKeyBytes += 80L + name.ByteLength;
                _path[^1] = Segment.AtKey(name);
                _next = null;
                _hasNext = false;
                _lastSeen = LastSeen.Colon;
                return StreamScanOutcome.NeedsMore(consumed);
            case ',':
                if (_lastSeen != LastSeen.Value)
                    return Blame(consumed, "Expected value before ','");
                if (_path.Count == 0)
                    return Blame(consumed, "',' not as part of an object or array");
                Segment top = _path[^1];
                if (top.IsIndex)
                {
                    // Separators emit the leaf against the pre-increment path
                    // with no close marker; only container ends stash one.
                    JqArray path = CopyPath();
                    bool haveLeaf = _hasNext;
                    JqValue? leaf = _next;
                    _next = null;
                    _hasNext = false;
                    _path[^1] = Segment.AtIndex(top.Index + 1);
                    _lastSeen = LastSeen.Comma;
                    if (haveLeaf)
                    {
                        _budget.CheckCancellation();
                        return StreamScanOutcome.WithEvent(consumed, new JqArray { path, leaf });
                    }
                    return StreamScanOutcome.NeedsMore(consumed);
                }
                if (!top.IsPending)
                {
                    JqArray path = CopyPath();
                    bool haveLeaf = _hasNext;
                    JqValue? leaf = _next;
                    _next = null;
                    _hasNext = false;
                    _pathKeyBytes -= (_path[^1].Key is { } previousKey ? 80L + previousKey.ByteLength : 0);
                    _path[^1] = Segment.Pending();
                    _lastSeen = LastSeen.Comma;
                    if (haveLeaf)
                    {
                        _budget.CheckCancellation();
                        return StreamScanOutcome.WithEvent(consumed, new JqArray { path, leaf });
                    }
                    return StreamScanOutcome.NeedsMore(consumed);
                }
                return Blame(consumed, "Objects must consist of key:value pairs");
            case ']':
                if (_path.Count == 0)
                    return Blame(consumed, "Unmatched ']' at the top-level");
                if (_lastSeen == LastSeen.Comma)
                    return Blame(consumed, "Expected another array element");
                if (!_path[^1].IsIndex)
                    return Blame(consumed, "Unmatched ']' in the middle of an object");
                return CloseContainer(consumed, isArray: true);
            default:
                if (_path.Count == 0)
                    return Blame(consumed, "Unmatched '}' at the top-level");
                if (_lastSeen == LastSeen.Comma)
                    return Blame(consumed, "Expected another key:value pair");
                if (_path[^1].IsIndex)
                    return Blame(consumed, "Unmatched '}' in the middle of an array");
                return CloseContainer(consumed, isArray: false);
        }
    }

    private StreamScanOutcome CloseContainer(int consumed, bool isArray)
    {
        bool fresh = isArray ? _lastSeen == LastSeen.OpenArray : _lastSeen == LastSeen.OpenObject;
        if (_hasNext)
        {
            if (!isArray && _path[^1].IsPending)
                return Blame(consumed, "Objects must consist of key:value pairs");
            JqValue? leaf = _next;
            _next = null;
            _hasNext = false;
            JqArray path = CopyPath();
            PopPath();
            _lastSeen = _path.Count == 0 ? LastSeen.None : LastSeen.Value;
            _budget.CheckCancellation();
            var emitted = new JqArray { path, leaf };
            _stashed = StashClose(path);
            return StreamScanOutcome.WithEvent(consumed, emitted);
        }
        if (!fresh)
        {
            if (!isArray)
            {
                if (_lastSeen == LastSeen.Colon)
                    return Blame(consumed, "Missing value in key:value pair");
                if (_lastSeen == LastSeen.Comma)
                    return Blame(consumed, "Expected another key-value pair");
                if (_lastSeen == LastSeen.OpenArray)
                    return Blame(consumed, "Unmatched '}' in the middle of an array");
                if (_lastSeen != LastSeen.Value && _lastSeen != LastSeen.OpenObject)
                    return Blame(consumed, "Unmatched '}'");
            }
            JqArray path = CopyPath();
            PopPath();
            _lastSeen = _path.Count == 0 ? LastSeen.None : LastSeen.Value;
            _budget.CheckCancellation();
            return StreamScanOutcome.WithEvent(consumed, new JqArray { path });
        }
        PopPath();
        _lastSeen = _path.Count == 0 ? LastSeen.None : LastSeen.Value;
        JqValue? empty = isArray ? (JqValue?)new JqArray() : new JqObject(_budget);
        return StreamScanOutcome.WithEvent(consumed, LeafEvent(CopyPath(), empty));
    }

    private JqArray StashClose(JqArray path)
    {
        _budget.ReserveBytes(88);
        var snapshot = new JqArray();
        _budget.ReserveBytes(64 + 88L * path.Count);
        long bytes = 152 + 88L * path.Count;
        foreach (JqValue? segment in path)
        {
            if (segment is JqString key) bytes += 80L + key.ByteLength;
            snapshot.Add(_budget.CopyValue(segment, 0));
        }
        _stashedBytes = bytes;
        return new JqArray { snapshot };
    }

    private void PopPath()
    {
        _pathKeyBytes -= (_path[^1].Key is { } previousKey ? 80L + previousKey.ByteLength : 0);
        _path.RemoveAt(_path.Count - 1);
    }

    private JqArray LeafEvent(JqArray path, JqValue? leaf)
    {
        _budget.CheckCancellation();
        return new JqArray { path, leaf };
    }

    private void PushIndex(long index)
    {
        JqRecursion.CheckDepth(_path.Count + 1, "value nesting limit exceeded");
        _path.Add(Segment.AtIndex(index));
    }

    private void PushPending()
    {
        JqRecursion.CheckDepth(_path.Count + 1, "value nesting limit exceeded");
        _path.Add(Segment.Pending());
    }

    private JqArray CopyPath()
    {
        var path = new JqArray();
        foreach (Segment segment in _path)
        {
            if (segment.IsIndex)
                path.Add(JqScalar.Create(segment.Index));
            else if (segment.Key is JqString key)
                path.Add(JqScalar.Create(key));
            else
                path.Add(null);
        }
        return path;
    }

    private void AppendToken(byte b)
    {
        if (_tokenLength >= _token.Length)
        {
            int capacity = JqBudget.BufferCapacity((long)_tokenLength + 1, Math.Max(256, 2L * _token.Length));
            _budget.CheckCancellation();
            _tokenMemory.ReplaceBuffer(ref _token, capacity, 0, _tokenLength, _budget.CancellationToken);
        }
        _token[_tokenLength++] = b;
    }

    // Blames the current position: fatal errors abort the pull, sequence
    // errors resynchronize past the separator, and `--stream-errors` emits
    // an `["message", path]` value and continues (dropping the buffered
    // remainder outside record framing, like the reference chunk model).
    private StreamScanOutcome Blame(int consumed, string baseMessage)
    {
        // Sequence framing turns every mid-record failure into resync
        // recovery; the RS record path reports truncation on its own.
        bool resync = _seq;
        string message = $"{baseMessage} at line {_line}, column {_column}";
        if (!_streamErrors)
        {
            if (resync)
            {
                ResetRecordState();
                _seqWaiting = true;
                return StreamScanOutcome.WithWarning(consumed, message + " (need RS to resync)");
            }
            throw new JqException(message);
        }
        string suffixed = resync ? message + " (need RS to resync)" : message;
        _budget.CheckCancellation();
        _budget.ReserveString(suffixed.Length);
        var errorEvent = new JqArray { JqScalar.Create(suffixed, _budget), CopyPath() };
        ResetRecordState();
        if (resync)
            _seqWaiting = true;
        return StreamScanOutcome.WithEvent(consumed, errorEvent, dropRest: !resync);
    }

    private void ResetRecordState()
    {
        _path.Clear();
        _pathKeyBytes = 0;
        _lastSeen = LastSeen.None;
        _next = null;
        _hasNext = false;
        _stashed = null;
        _tokenLength = 0;
        _inString = false;
        _stringToken = default;
    }

    private StreamScanOutcome EndStreamRecord(int consumed)
    {
        if (_path.Count > 0)
            return TruncatedRecord(consumed, "Truncated value");
        if (_tokenLength > 0)
        {
            if (!TryDecodeToken(out JqValue? scalar))
                return TruncatedRecord(consumed, TokenStartsWithLiteral(_inString ? 1 : 0)
                    ? "Invalid literal" : "Invalid numeric literal");
            ResetRecordState();
            return StreamScanOutcome.WithEvent(consumed, LeafEvent(new JqArray(), scalar));
        }
        if (_hasNext)
        {
            JqValue? pending = _next;
            _next = null;
            _hasNext = false;
            ResetRecordState();
            return StreamScanOutcome.WithEvent(consumed, LeafEvent(new JqArray(), pending));
        }
        ResetRecordState();
        return StreamScanOutcome.NeedsMore(consumed);
    }

    private StreamScanOutcome TruncatedRecord(int consumed, string baseMessage)
    {
        string message = $"{baseMessage} at line {_line}, column {_column}";
        JqArray? errorPath = _streamErrors ? CopyPath() : null;
        ResetRecordState();
        // This separator has already started the next record.
        _seqWaiting = false;
        if (_streamErrors)
        {
            _budget.CheckCancellation();
            _budget.ReserveString(message.Length);
            var errorEvent = new JqArray { JqScalar.Create(message, _budget), errorPath };
            return StreamScanOutcome.WithEvent(consumed, errorEvent, dropRest: false);
        }
        return StreamScanOutcome.WithWarning(consumed, message);
    }

    private bool TryDecodeToken(out JqValue? scalar)
    {
        scalar = null;
        try
        {
            JqValue? decoded = _runtime.ReadJsonValue(_token.AsSpan(0, _tokenLength), out int used);
            if (used != _tokenLength || decoded is not null and not JqScalar)
                return false;
            scalar = decoded;
            return true;
        }
        catch (Exception ex) when (ex is JqException and not JqQuotaException
            or System.Text.Json.JsonException
            or InvalidOperationException)
        {
            return false;
        }
    }

    internal StreamScanOutcome FinishFinal()
    {
        if (_eofConcluded)
            return StreamScanOutcome.Finished();
        _eofConcluded = true;
        if (_inString)
        {
            _eofConcluded = true;
            JqArray errorPath = CopyPath();
            ResetRecordState();
            return EofOutcome("Unfinished string", errorPath);
        }
        if (_tokenLength > 0)
        {
            bool decoded = TryDecodeToken(out JqValue? scalar);
            bool literalStart = TokenStartsWithLiteral(0);
            _tokenLength = 0;
            if (!decoded)
            {
                JqArray errorPath = CopyPath();
                ResetRecordState();
                return EofOutcome(literalStart ? "Invalid literal" : "Invalid numeric literal", errorPath);
            }
            string? separator = BindValue(scalar);
            if (separator is not null)
            {
                JqArray errorPath = CopyPath();
                ResetRecordState();
                return EofOutcome(separator, errorPath);
            }
            if (_path.Count > 0)
            {
                JqArray errorPath = CopyPath();
                ResetRecordState();
                return EofOutcome("Unfinished JSON term", errorPath);
            }
            if (_seq && scalar is JqScalar number
                && (number.TryGetValue<long>(out _) || number.TryGetValue<double>(out _))
                && !_lastWasWs)
            {
                JqArray errorPath = CopyPath();
                ResetRecordState();
                return EofOutcome("Potentially truncated top-level numeric value", errorPath);
            }
            JqValue? emitted = CheckDone();
            ResetRecordState();
            if (emitted is not null)
                return StreamScanOutcome.WithEvent(0, emitted);
            return StreamScanOutcome.Finished();
        }
        if (_path.Count > 0)
        {
            _eofConcluded = true;
            JqArray errorPath = CopyPath();
            ResetRecordState();
            return EofOutcome("Unfinished JSON term", errorPath);
        }
        if (_hasNext)
        {
            JqValue? pending = _next;
            _next = null;
            _hasNext = false;
            _eofConcluded = true;
            JqArray pendingPath = CopyPath();
            ResetRecordState();
            if (_seq && pending is JqScalar number
                && (number.TryGetValue<long>(out _) || number.TryGetValue<double>(out _))
                && !_lastWasWs)
            {
                return EofOutcome("Potentially truncated top-level numeric value", pendingPath);
            }
            return StreamScanOutcome.WithEvent(0, LeafEvent(new JqArray(), pending));
        }
        if (_seq && _seqWaiting)
        {
            _eofConcluded = true;
            return EofOutcome("Unfinished abandoned text", CopyPath());
        }
        _eofConcluded = true;
        return StreamScanOutcome.Finished();
    }

    // True when the pending token starts like a non-numeric literal
    // (`true`, `false`, or the `nu` prefix), mirroring the reference
    // literal tables for diagnostics.
    private bool TokenStartsWithLiteral(int start)
    {
        if (_tokenLength <= start)
            return false;
        if (_token[start] == (byte)116 || _token[start] == (byte)102)
            return true;
        return _tokenLength >= start + 2 && _token[start] == (byte)110 && _token[start + 1] == (byte)117;
    }

    // EOF failures retain the live open-container path captured before reset.
    private StreamScanOutcome EofOutcome(string baseMessage, JqArray errorPath)
    {
        string message = $"{baseMessage} at EOF at line {_line}, column {_column}";
        if (_streamErrors)
        {
            _budget.CheckCancellation();
            _budget.ReserveString(message.Length);
            var errorEvent = new JqArray { JqScalar.Create(message, _budget), errorPath };
            return StreamScanOutcome.WithEvent(0, errorEvent, dropRest: false);
        }
        if (_seq)
            throw new JqSeqResyncException(message);
        throw new JqException(message);
    }
}
