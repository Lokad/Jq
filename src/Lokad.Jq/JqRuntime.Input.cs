using System.Collections.Generic;
using System.Text.Json;

namespace Lokad.Jq;

internal sealed partial class JqRuntime
{
    // Keeps the standard JSON reader and DOM stack across host reads. The cursor
    // retires committed bytes before further reads. Extended literals share
    // the whole-value token reader; invalid input fails without another host read.
    internal sealed class InputReader(JqRuntime runtime, JqBudget budget)
    {
        private static readonly JsonReaderOptions Options = new()
        {
            AllowTrailingCommas = false,
            AllowMultipleValues = true,
            MaxDepth = JqRecursion.MaximumDepth + 1
        };

        private readonly List<Frame> _frames = new();
        private JsonReaderState _state = new(Options);
        private InputPrefix _prefix;
        private JqValue? _root;
        private JsonReaderState _propertyState;
        private bool _propertyConsumesComma;
        private PendingToken _pending;

        private readonly record struct Frame(JqValue Container, JqString? Key);

        internal int ContainerDepth => _frames.Count;

        internal void Reset()
        {
            _state = new JsonReaderState(Options);
            _prefix = default;
            _root = null;
            _frames.Clear();
            _pending = default;
        }

        internal bool TryRead(ReadOnlySpan<byte> source, bool isFinal, out JqValue? value, out int consumed)
        {
            value = null;
            consumed = 0;
            // Utf8JsonReader retains completed-token state, not a partial
            // token's scan position. Inspect only appended bytes until they
            // can complete the token or require its normal error handling.
            if (!isFinal && _pending.Advance(source, budget, false)) return false;
            _pending = default;
            // Root literals stop at their own boundary, leaving following
            // whitespace and line accounting to the input cursor.
            if (_frames.Count == 0 && TryMatchNonFinite(source, 0, out double number, out int end)
                && (isFinal || end < source.Length))
            {
                budget.ReserveValueNode();
                value = JqScalar.Create(number);
                consumed = end;
                Reset();
                return true;
            }
            try
            {
                int consumedBase = 0;
                var reader = new Utf8JsonReader(source, isFinal, _state);
                while (true)
                {
                    JsonReaderState checkpointState = reader.CurrentState;
                    JsonTokenType previous = reader.TokenType;
                    bool propertyValue = _frames.Count != 0 && _frames[^1].Key is not null;
                    runtime.ReadToken(ref reader, source, ref consumedBase, _prefix,
                        propertyValue ? _propertyState : checkpointState,
                        propertyValue ? _propertyConsumesComma : previous == JsonTokenType.StartArray,
                        out bool hasToken, out JqValue? nonFinite);
                    if (!hasToken) break;
                    budget.CheckCancellation();
                    if (nonFinite is not null)
                        Attach(nonFinite);
                    else
                    {
                        switch (reader.TokenType)
                        {
                            case JsonTokenType.StartArray:
                            case JsonTokenType.StartObject:
                                JqRecursion.CheckDepth(_frames.Count + 1, "value nesting limit exceeded");
                                budget.ReserveValueNode();
                                if (_frames.Count == _frames.Capacity)
                                {
                                    int capacity = System.Math.Min(JqRecursion.MaximumDepth,
                                        System.Math.Max(4, _frames.Capacity * 2));
                                    budget.CheckCancellation();
                                    _frames.Capacity = capacity;
                                }
                                JqValue container = reader.TokenType == JsonTokenType.StartArray
                                    ? new JqArray() : new JqObject(budget);
                                Attach(container);
                                _frames.Add(new Frame(container, null));
                                break;
                            case JsonTokenType.PropertyName:
                                Frame frame = _frames[^1];
                                _frames[^1] = frame with { Key = JqString.FromJson(ref reader, budget) };
                                _propertyState = checkpointState;
                                _propertyConsumesComma = previous == JsonTokenType.StartObject;
                                break;
                            case JsonTokenType.EndArray:
                            case JsonTokenType.EndObject:
                                _frames.RemoveAt(_frames.Count - 1);
                                break;
                            default:
                                Attach(runtime.ReadScalar(ref reader));
                                break;
                        }
                    }
                    if (_frames.Count == 0)
                    {
                        consumed = consumedBase + (int)reader.BytesConsumed;
                        budget.CheckCancellation();
                        value = _root;
                        Reset();
                        return true;
                    }
                }
                consumed = consumedBase + (int)reader.BytesConsumed;
                _state = reader.CurrentState;
                _prefix.Advance(source[..consumed], budget);
                if (!isFinal)
                {
                    _pending.Start(source[consumed..], budget);
                    return false;
                }
            }
            catch (JsonException exception)
            {
                throw new JqException(exception.Message, exception);
            }
            throw new JqException("truncated JSON value");

            void Attach(JqValue? node)
            {
                if (_frames.Count == 0)
                    _root = node;
                else if (_frames[^1].Container is JqArray array)
                {
                    budget.ReserveBytes(24);
                    array.Add(node);
                }
                else
                {
                    Frame frame = _frames[^1];
                    if (frame.Container is not JqObject obj || frame.Key is not JqString key)
                        throw new System.InvalidOperationException("Expected a JSON object property.");
                    budget.ReserveBytes(96);
                    obj[key] = node;
                    _frames[^1] = frame with { Key = null };
                }
            }
        }

        private struct PendingToken
        {
            private enum Kind { None, String, PropertySeparator, Number }
            private Kind _kind;
            private int _scanned;
            private JqStringToken _string;
            private bool _signOnly;
            private bool _zero;

            internal void Start(ReadOnlySpan<byte> source, JqBudget budget)
            {
                int start = SkipJsonWhitespace(source, 0);
                if (start < source.Length && source[start] == (byte)',')
                    start = SkipJsonWhitespace(source, start + 1);
                if (start == source.Length) return;
                if (source[start] == (byte)'"')
                {
                    _kind = Kind.String;
                    _scanned = start + 1;
                    if (!Advance(source, budget, true)) this = default;
                }
                else if (source[start] is >= (byte)'0' and <= (byte)'9'
                    || source[start] == (byte)'-' && (start + 1 == source.Length
                        || source[start + 1] is >= (byte)'0' and <= (byte)'9'))
                {
                    // The reader has already validated this numeric prefix.
                    // Appended digits are safe except after a leading zero.
                    _kind = Kind.Number;
                    _scanned = source.Length;
                    _signOnly = start + 1 == source.Length && source[start] == (byte)'-';
                    _zero = source[start..].SequenceEqual("0"u8) || source[start..].SequenceEqual("-0"u8);
                }
            }

            internal bool Advance(ReadOnlySpan<byte> source, JqBudget budget, bool starting)
            {
                if (_kind == Kind.None || source.Length < _scanned) return false;
                while (_scanned < source.Length)
                {
                    budget.CheckCancellation();
                    int length = Math.Min(4096, source.Length - _scanned);
                    ReadOnlySpan<byte> block = source.Slice(_scanned, length);
                    if (_kind == Kind.String && _string.IsPlain)
                    {
                        int marker = block.IndexOfAny((byte)'"', (byte)'\\');
                        int control = block.IndexOfAnyInRange((byte)0, (byte)31);
                        if (marker < 0) marker = control;
                        else if (control >= 0) marker = Math.Min(marker, control);
                        if (marker < 0) { _scanned += length; continue; }
                        _scanned += marker;
                    }
                    else if (_kind == Kind.Number && !_signOnly && !_zero)
                    {
                        if (block.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0) return false;
                        _scanned += length;
                        continue;
                    }
                    byte current = source[_scanned++];
                    if (_kind == Kind.String)
                    {
                        if (!_string.Advance(current, out bool closed)) return false;
                        if (closed)
                        {
                            _kind = Kind.PropertySeparator;
                            if (!starting) return false;
                        }
                    }
                    else if (_kind == Kind.PropertySeparator)
                    {
                        if (current is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
                    }
                    else
                    {
                        if (_zero || current is < (byte)'0' or > (byte)'9') return false;
                        _zero = _signOnly && current == (byte)'0';
                        _signOnly = false;
                    }
                }
                return true;
            }
        }
    }
}
