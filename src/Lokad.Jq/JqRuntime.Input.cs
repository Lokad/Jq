using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        private JsonNode? _root;
        private JsonReaderState _propertyState;
        private bool _propertyConsumesComma;

        private readonly record struct Frame(JsonNode Container, string? Key);

        internal void Reset()
        {
            _state = new JsonReaderState(Options);
            _prefix = default;
            _root = null;
            _frames.Clear();
        }

        internal bool TryRead(ReadOnlySpan<byte> source, bool isFinal, out JsonNode? value, out int consumed)
        {
            value = null;
            consumed = 0;
            // Root literals stop at their own boundary, leaving following
            // whitespace and line accounting to the input cursor.
            if (_frames.Count == 0 && TryMatchNonFinite(source, 0, out double number, out int end)
                && (isFinal || end < source.Length))
            {
                budget.ReserveValueNode();
                value = JsonValue.Create(number);
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
                        out bool hasToken, out JsonNode? nonFinite);
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
                                JsonNode container = reader.TokenType == JsonTokenType.StartArray
                                    ? new JsonArray() : new JsonObject();
                                Attach(container);
                                _frames.Add(new Frame(container, null));
                                break;
                            case JsonTokenType.PropertyName:
                                Frame frame = _frames[^1];
                                _frames[^1] = frame with { Key = runtime.ReadAdmittedString(ref reader) };
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
                if (!isFinal) return false;
            }
            catch (JsonException exception)
            {
                throw new JqException(exception.Message, exception);
            }
            throw new JqException("truncated JSON value");

            void Attach(JsonNode? node)
            {
                if (_frames.Count == 0)
                    _root = node;
                else if (_frames[^1].Container is JsonArray array)
                {
                    budget.ReserveBytes(24);
                    array.Add(node);
                }
                else
                {
                    Frame frame = _frames[^1];
                    if (frame.Container is not JsonObject obj || frame.Key is not string key)
                        throw new System.InvalidOperationException("Expected a JSON object property.");
                    budget.ReserveBytes(96);
                    obj[key] = node;
                    _frames[^1] = frame with { Key = null };
                }
            }
        }
    }
}
