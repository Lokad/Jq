using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// Outcome of one streaming scan step: bytes to advance past plus at most
// one event or error. Fatal and resync errors travel as exceptions so the
// cursor positions the buffer before propagating them.
internal readonly record struct StreamScanOutcome(
    int Consumed,
    JsonNode? Event,
    JsonNode? ErrorEvent,
    JqException? Error,
    bool DropRest)
{
    public static StreamScanOutcome NeedsMore(int consumed) =>
        new(consumed, null, null, null, false);

    public static StreamScanOutcome WithEvent(int consumed, JsonNode? @event) =>
        new(consumed, @event, null, null, false);

    public static StreamScanOutcome WithErrorEvent(int consumed, JsonNode? errorEvent, bool dropRest) =>
        new(consumed, null, errorEvent, null, dropRest);

    public static StreamScanOutcome Finished() =>
        new(0, null, null, null, false);
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
internal sealed class JqStreamScanner
{
    private const int MaxPathDepth = 64;

    private readonly JqBudget _budget;
    private readonly JqRuntime _runtime;
    private readonly bool _seq;
    private readonly bool _streamErrors;

    private enum LastSeen { None, OpenArray, OpenObject, Colon, Comma, Value }

    private readonly struct Segment
    {
        public readonly bool IsIndex;
        public readonly long Index;
        public readonly string? Key;

        private Segment(bool isIndex, long index, string? key)
        {
            IsIndex = isIndex;
            Index = index;
            Key = key;
        }

        public static Segment Pending() => new(false, 0, null);

        public static Segment AtIndex(long index) => new(true, index, null);

        public static Segment AtKey(string key) => new(false, 0, key);

        public bool IsPending => !IsIndex && Key is null;
    }

    private readonly List<Segment> _path = new();
    private LastSeen _lastSeen;
    private JsonNode? _next;
    private bool _hasNext;
    private JsonNode? _stashed;
    private byte[] _token = [];
    private int _tokenLength;
    private bool _inString;
    private bool _escaped;
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
        _seq = seq;
        _streamErrors = streamErrors;
        _seqWaiting = seq;
    }

    internal bool TryDrainStash(out JsonNode? @event)
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

    // Scans bytes until the first event, error, or stall. With isFinal, also
    // runs end-of-input finalization. Consumed stops at the completed event
    // so later bytes wait for the next pull.
    internal StreamScanOutcome Consume(ReadOnlySpan<byte> input, bool isFinal)
    {
        if (TryDrainStash(out JsonNode? stashed))
            return StreamScanOutcome.WithEvent(0, stashed);
        for (var i = 0; i < input.Length; i++)
        {
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
                if (_escaped)
                    _escaped = false;
                else if (b == (byte)92)
                    _escaped = true;
                else if (b == (byte)34)
                    return FinishString(i + 1);
                continue;
            }
            if (IsStreamWhitespace(b))
            {
                _lastWasWs = true;
                if (_tokenLength > 0)
                {
                    StreamScanOutcome? literal = FinishLiteral(i);
                    if (literal is not null)
                        return literal.Value;
                }
                continue;
            }
            if (b == (byte)34)
            {
                if (_tokenLength > 0)
                {
                    StreamScanOutcome? literal = FinishLiteral(i);
                    if (literal is not null)
                        return literal.Value;
                }
                _inString = true;
                _escaped = false;
                AppendToken(b);
                continue;
            }
            if (b == (byte)91 || b == (byte)93 || b == (byte)123 || b == (byte)125
                || b == (byte)58 || b == (byte)44)
            {
                if (_tokenLength > 0)
                {
                    StreamScanOutcome? literal = FinishLiteral(i);
                    if (literal is not null)
                        return literal.Value;
                }
                return Structure((char)b, i + 1);
            }
            AppendToken(b);
        }
        if (isFinal)
            return FinishFinal();
        return StreamScanOutcome.NeedsMore(input.Length);
    }

    private static bool IsStreamWhitespace(byte b) =>
        b == (byte)32 || b == (byte)9 || b == (byte)10 || b == (byte)13;

    // A literal token completed at a delimiter: decode it, bind it as the
    // pending value, and report a top-level scalar event when ready. Null
    // means scanning continues after the delimiter.
    private StreamScanOutcome? FinishLiteral(int consumed)
    {
        JsonNode? scalar;
        try
        {
            scalar = _runtime.ReadJsonValue(_token.AsSpan(0, _tokenLength), out int used);
            if (used != _tokenLength)
            {
                _tokenLength = 0;
                return Blame(consumed, LiteralError(), midResync: false);
            }
        }
        catch (JqException ex) when (ex is not JqQuotaException)
        {
            _tokenLength = 0;
            return Blame(consumed, LiteralError(), midResync: false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            _tokenLength = 0;
            return Blame(consumed, LiteralError(), midResync: false);
        }
        _tokenLength = 0;
        string? separator = BindValue(scalar);
        if (separator is not null)
            return Blame(consumed, separator, midResync: false);
        JsonNode? emitted = CheckDone();
        return emitted is null
            ? null
            : StreamScanOutcome.WithEvent(consumed, emitted);
    }

    private string LiteralError()
    {
        if (_tokenLength == 0)
            return "Invalid literal";
        byte first = _token[0];
        if (first == (byte)116 || first == (byte)102)
            return "Invalid literal";
        if (_tokenLength >= 2 && first == (byte)110 && _token[1] == (byte)117)
            return "Invalid literal";
        return "Invalid numeric literal";
    }

    private StreamScanOutcome FinishString(int consumed)
    {
        JsonNode? scalar;
        try
        {
            scalar = _runtime.ReadJsonValue(_token.AsSpan(0, _tokenLength), out int used);
            if (used != _tokenLength || scalar is not JsonValue)
                return Blame(consumed, "Invalid string", midResync: false);
        }
        catch (JqException ex) when (ex is not JqQuotaException)
        {
            _tokenLength = 0;
            _inString = false;
            return Blame(consumed, ex.Message, midResync: false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            _tokenLength = 0;
            _inString = false;
            return Blame(consumed, "Invalid string", midResync: false);
        }
        _tokenLength = 0;
        _inString = false;
        string? separator = BindValue(scalar);
        if (separator is not null)
            return Blame(consumed, separator, midResync: false);
        JsonNode? emitted = CheckDone();
        return emitted is null
            ? StreamScanOutcome.NeedsMore(consumed)
            : StreamScanOutcome.WithEvent(consumed, emitted);
    }

    // Binds a decoded scalar as the pending value. Returns a separator
    // diagnostic when another value is already pending.
    private string? BindValue(JsonNode? scalar)
    {
        if (_hasNext || _lastSeen == LastSeen.Value)
            return "Expected separator between values";
        _next = scalar;
        _hasNext = true;
        _lastSeen = _path.Count > 0 ? LastSeen.Value : LastSeen.None;
        return null;
    }

    // Emits a completed top-level scalar, if any is pending.
    private JsonNode? CheckDone()
    {
        if (_path.Count == 0 && _hasNext)
        {
            JsonNode? leaf = _next;
            _next = null;
            _hasNext = false;
            return LeafEvent(new JsonArray(), leaf);
        }
        return null;
    }

    private StreamScanOutcome Structure(char op, int consumed)
    {
        switch (op)
        {
            case '[':
                if (_hasNext)
                    return Blame(consumed, "Expected a separator between values", midResync: false);
                if (_lastSeen == LastSeen.OpenObject)
                    return Blame(consumed, "Expected string key after '{', not '['", midResync: false);
                if (_lastSeen == LastSeen.Comma && !_path[^1].IsIndex)
                    return Blame(consumed, "Expected string key after ',' in object, not '['", midResync: false);
                PushIndex(0);
                _lastSeen = LastSeen.OpenArray;
                return StreamScanOutcome.NeedsMore(consumed);
            case '{':
                if (_lastSeen == LastSeen.Value)
                    return Blame(consumed, "Expected a separator between values", midResync: false);
                if (_lastSeen == LastSeen.OpenObject)
                    return Blame(consumed, "Expected string key after '{', not '{'", midResync: false);
                if (_lastSeen == LastSeen.Comma && !_path[^1].IsIndex)
                    return Blame(consumed, "Expected string key after ',' in object, not '{'", midResync: false);
                PushPending();
                _lastSeen = LastSeen.OpenObject;
                return StreamScanOutcome.NeedsMore(consumed);
            case ':':
                if (_path.Count == 0 || _path[^1].IsIndex)
                    return Blame(consumed, "':' not as part of an object", midResync: false);
                if (!_hasNext)
                    return Blame(consumed, "Expected string key before ':'", midResync: false);
                if (_next is not JsonValue key || !JqRuntime.TryGetString(key, out string? name) || name is null)
                    return Blame(consumed, "Object keys must be strings", midResync: false);
                if (_lastSeen != LastSeen.Value)
                    return Blame(consumed, "':' should follow a key", midResync: false);
                _path[^1] = Segment.AtKey(name);
                _next = null;
                _hasNext = false;
                _lastSeen = LastSeen.Colon;
                return StreamScanOutcome.NeedsMore(consumed);
            case ',':
                if (_lastSeen != LastSeen.Value)
                    return Blame(consumed, "Expected value before ','", midResync: false);
                if (_path.Count == 0)
                    return Blame(consumed, "',' not as part of an object or array", midResync: false);
                Segment top = _path[^1];
                if (top.IsIndex)
                {
                    // Separators emit the leaf against the pre-increment path
                    // with no close marker; only container ends stash one.
                    JsonArray path = CopyPath();
                    bool haveLeaf = _hasNext;
                    JsonNode? leaf = _next;
                    _next = null;
                    _hasNext = false;
                    _path[^1] = Segment.AtIndex(top.Index + 1);
                    _lastSeen = LastSeen.Comma;
                    if (haveLeaf)
                    {
                        _budget.ChargeNode();
                        return StreamScanOutcome.WithEvent(consumed, new JsonArray { path, leaf });
                    }
                    return StreamScanOutcome.NeedsMore(consumed);
                }
                if (!top.IsPending)
                {
                    JsonArray path = CopyPath();
                    bool haveLeaf = _hasNext;
                    JsonNode? leaf = _next;
                    _next = null;
                    _hasNext = false;
                    _path[^1] = Segment.Pending();
                    _lastSeen = LastSeen.Comma;
                    if (haveLeaf)
                    {
                        _budget.ChargeNode();
                        return StreamScanOutcome.WithEvent(consumed, new JsonArray { path, leaf });
                    }
                    return StreamScanOutcome.NeedsMore(consumed);
                }
                return Blame(consumed, "Objects must consist of key:value pairs", midResync: false);
            case ']':
                if (_path.Count == 0)
                    return Blame(consumed, "Unmatched ']' at the top-level", midResync: false);
                if (_lastSeen == LastSeen.Comma)
                    return Blame(consumed, "Expected another array element", midResync: false);
                if (!_path[^1].IsIndex)
                    return Blame(consumed, "Unmatched ']' in the middle of an object", midResync: false);
                return CloseContainer(consumed, isArray: true);
            default:
                if (_path.Count == 0)
                    return Blame(consumed, "Unmatched '}' at the top-level", midResync: false);
                if (_lastSeen == LastSeen.Comma)
                    return Blame(consumed, "Expected another key:value pair", midResync: false);
                if (_path[^1].IsIndex)
                    return Blame(consumed, "Unmatched '}' in the middle of an array", midResync: false);
                return CloseContainer(consumed, isArray: false);
        }
    }

    private StreamScanOutcome CloseContainer(int consumed, bool isArray)
    {
        bool fresh = isArray ? _lastSeen == LastSeen.OpenArray : _lastSeen == LastSeen.OpenObject;
        if (_hasNext)
        {
            if (!isArray && _path[^1].IsPending)
                return Blame(consumed, "Objects must consist of key:value pairs", midResync: false);
            JsonNode? leaf = _next;
            _next = null;
            _hasNext = false;
            JsonArray path = CopyPath();
            _path.RemoveAt(_path.Count - 1);
            _lastSeen = _path.Count == 0 ? LastSeen.None : LastSeen.Value;
            _budget.ChargeNode();
            var emitted = new JsonArray { path, leaf };
            _stashed = StashClose(path);
            return StreamScanOutcome.WithEvent(consumed, emitted);
        }
        if (!fresh)
        {
            if (!isArray)
            {
                if (_lastSeen == LastSeen.Colon)
                    return Blame(consumed, "Missing value in key:value pair", midResync: false);
                if (_lastSeen == LastSeen.Comma)
                    return Blame(consumed, "Expected another key-value pair", midResync: false);
                if (_lastSeen == LastSeen.OpenArray)
                    return Blame(consumed, "Unmatched '}' in the middle of an array", midResync: false);
                if (_lastSeen != LastSeen.Value && _lastSeen != LastSeen.OpenObject)
                    return Blame(consumed, "Unmatched '}'", midResync: false);
            }
            JsonArray path = CopyPath();
            _path.RemoveAt(_path.Count - 1);
            _lastSeen = _path.Count == 0 ? LastSeen.None : LastSeen.Value;
            _budget.ChargeNode();
            return StreamScanOutcome.WithEvent(consumed, new JsonArray { path });
        }
        _path.RemoveAt(_path.Count - 1);
        _lastSeen = _path.Count == 0 ? LastSeen.None : LastSeen.Value;
        JsonNode? empty = isArray ? (JsonNode?)new JsonArray() : new JsonObject();
        return StreamScanOutcome.WithEvent(consumed, LeafEvent(CopyPath(), empty));
    }

    private StreamScanOutcome LeafInContainer(int consumed, JsonNode? leaf)
    {
        JsonArray path = CopyPath();
        _budget.ChargeNode();
        var emitted = new JsonArray { path, leaf };
        _stashed = StashClose(path);
        return StreamScanOutcome.WithEvent(consumed, emitted);
    }

    private static JsonArray StashClose(JsonArray path)
    {
        JsonNode? clone = path.DeepClone();
        JsonArray snapshot = clone is JsonArray array ? array : new JsonArray();
        return new JsonArray { snapshot };
    }

    private JsonArray LeafEvent(JsonArray path, JsonNode? leaf)
    {
        _budget.ChargeNode();
        return new JsonArray { path, leaf };
    }

    private void PushIndex(long index)
    {
        if (_path.Count >= MaxPathDepth)
            throw new JqInputException("value nesting limit exceeded", 5);
        _path.Add(Segment.AtIndex(index));
    }

    private void PushPending()
    {
        if (_path.Count >= MaxPathDepth)
            throw new JqInputException("value nesting limit exceeded", 5);
        _path.Add(Segment.Pending());
    }

    private JsonArray CopyPath()
    {
        var path = new JsonArray();
        foreach (Segment segment in _path)
        {
            if (segment.IsIndex)
                path.Add(JsonValue.Create(segment.Index));
            else if (segment.Key is string key)
                path.Add(JsonValue.Create(key));
            else
                path.Add(null);
        }
        return path;
    }

    private void AppendToken(byte b)
    {
        if (_tokenLength >= _token.Length)
        {
            int capacity = _token.Length == 0 ? 256 : _token.Length * 2;
            _budget.ChargeBytes(capacity);
            Array.Resize(ref _token, capacity);
        }
        _token[_tokenLength++] = b;
    }

    // Blames the current position: fatal errors abort the pull, sequence
    // errors resynchronize past the separator, and `--stream-errors` emits
    // an `["message", path]` value and continues (dropping the buffered
    // remainder outside record framing, like the reference chunk model).
    private StreamScanOutcome Blame(int consumed, string baseMessage, bool midResync)
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
                throw new JqSeqResyncException(message + " (need RS to resync)");
            }
            throw new JqException(message);
        }
        string suffixed = resync ? message + " (need RS to resync)" : message;
        _budget.ChargeNode();
        _budget.ChargeString(suffixed.Length);
        var errorEvent = new JsonArray { JsonValue.Create(suffixed), CopyPath() };
        ResetRecordState();
        if (resync)
            _seqWaiting = true;
        return StreamScanOutcome.WithErrorEvent(consumed, errorEvent, dropRest: !resync);
    }

    private void ResetRecordState()
    {
        _path.Clear();
        _lastSeen = LastSeen.None;
        _next = null;
        _hasNext = false;
        _stashed = null;
        _tokenLength = 0;
        _inString = false;
        _escaped = false;
    }

    private StreamScanOutcome EndStreamRecord(int consumed)
    {
        if (_path.Count > 0)
            return TruncatedRecord(consumed, numeric: false);
        if (_tokenLength > 0)
        {
            if (TryDecodeTokenAsNumber(out _))
                return TruncatedRecord(consumed, numeric: true);
            return TruncatedRecord(consumed, numeric: false);
        }
        if (_hasNext)
        {
            JsonNode? pending = _next;
            _next = null;
            _hasNext = false;
            ResetRecordState();
            return StreamScanOutcome.WithEvent(consumed, LeafEvent(new JsonArray(), pending));
        }
        ResetRecordState();
        return StreamScanOutcome.NeedsMore(consumed);
    }

    private StreamScanOutcome TruncatedRecord(int consumed, bool numeric)
    {
        string baseMessage = numeric
            ? "Potentially truncated top-level numeric value"
            : "Truncated value";
        ResetRecordState();
        _seqWaiting = true;
        if (_streamErrors)
        {
            string message = $"{baseMessage} at line {_line}, column {_column} (need RS to resync)";
            _budget.ChargeNode();
            _budget.ChargeString(message.Length);
            var errorEvent = new JsonArray { JsonValue.Create(message), CopyPath() };
            return StreamScanOutcome.WithErrorEvent(consumed, errorEvent, dropRest: false);
        }
        throw new JqSeqResyncException($"{baseMessage} at line {_line}, column {_column} (need RS to resync)");
    }

    private bool TryDecodeTokenAsNumber(out JsonNode? scalar)
    {
        scalar = null;
        try
        {
            JsonNode? decoded = _runtime.ReadJsonValue(_token.AsSpan(0, _tokenLength), out int used);
            if (used != _tokenLength || decoded is not JsonValue value)
                return false;
            if (value.TryGetValue<long>(out _) || value.TryGetValue<double>(out _))
            {
                scalar = decoded;
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is JqException
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
            JsonArray errorPath = CopyPath();
            ResetRecordState();
            return EofOutcome("Unfinished string", errorPath);
        }
        if (_tokenLength > 0)
        {
            JsonNode? scalar;
            bool decoded;
            try
            {
                scalar = _runtime.ReadJsonValue(_token.AsSpan(0, _tokenLength), out int used);
                decoded = used == _tokenLength;
            }
            catch (Exception ex) when (ex is JqException
                or System.Text.Json.JsonException
                or InvalidOperationException)
            {
                scalar = null;
                decoded = false;
            }
            bool literalStart = TokenStartsWithLiteral();
            _tokenLength = 0;
            if (!decoded)
            {
                JsonArray errorPath = CopyPath();
                ResetRecordState();
                return EofOutcome(literalStart ? "Invalid literal" : "Invalid numeric literal", errorPath);
            }
            string? separator = BindValue(scalar);
            if (separator is not null)
            {
                JsonArray errorPath = CopyPath();
                ResetRecordState();
                return EofOutcome(separator, errorPath);
            }
            if (_path.Count > 0)
            {
                JsonArray errorPath = CopyPath();
                ResetRecordState();
                return EofOutcome("Unfinished JSON term", errorPath);
            }
            if (_seq && scalar is JsonValue number
                && (number.TryGetValue<long>(out _) || number.TryGetValue<double>(out _))
                && !_lastWasWs)
            {
                JsonArray errorPath = CopyPath();
                ResetRecordState();
                return EofOutcome("Potentially truncated top-level numeric value", errorPath);
            }
            JsonNode? emitted = CheckDone();
            ResetRecordState();
            if (emitted is not null)
                return StreamScanOutcome.WithEvent(0, emitted);
            return StreamScanOutcome.Finished();
        }
        if (_path.Count > 0)
        {
            _eofConcluded = true;
            JsonArray errorPath = CopyPath();
            ResetRecordState();
            return EofOutcome("Unfinished JSON term", errorPath);
        }
        if (_hasNext)
        {
            JsonNode? pending = _next;
            _next = null;
            _hasNext = false;
            _eofConcluded = true;
            JsonArray pendingPath = CopyPath();
            ResetRecordState();
            if (_seq && pending is JsonValue number
                && (number.TryGetValue<long>(out _) || number.TryGetValue<double>(out _))
                && !_lastWasWs)
            {
                return EofOutcome("Potentially truncated top-level numeric value", pendingPath);
            }
            return StreamScanOutcome.WithEvent(0, LeafEvent(new JsonArray(), pending));
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
    // literal tables for end-of-input diagnostics.
    private bool TokenStartsWithLiteral()
    {
        if (_tokenLength == 0)
            return false;
        if (_token[0] == (byte)116 || _token[0] == (byte)102)
            return true;
        return _tokenLength >= 2 && _token[0] == (byte)110 && _token[1] == (byte)117;
    }

    // End-of-input failures report the open-container path snapshotted by
    // the caller: resetting first (as before) would always yield [] while
    // the reference reports the live path (for example [0] after `[`).
    private StreamScanOutcome EofOutcome(string baseMessage, JsonArray errorPath)
    {
        string message = $"{baseMessage} at EOF at line {_line}, column {_column}";
        if (_streamErrors)
        {
            _budget.ChargeNode();
            _budget.ChargeString(message.Length);
            var errorEvent = new JsonArray { JsonValue.Create(message), errorPath };
            return StreamScanOutcome.WithErrorEvent(0, errorEvent, dropRest: false);
        }
        if (_seq)
            throw new JqSeqResyncException(message);
        throw new JqException(message);
    }
}
