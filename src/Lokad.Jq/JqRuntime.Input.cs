using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

internal sealed partial class JqRuntime
{
    // Keeps the standard JSON reader and DOM stack across host reads. The cursor
    // retains the original bytes until a value completes, so extended literals
    // and malformed input can use the established whole-value reader/diagnostics.
    internal sealed class InputReader(JqRuntime runtime, JqBudget budget)
    {
        private static readonly JsonReaderOptions Options = new()
        {
            AllowTrailingCommas = false,
            AllowMultipleValues = true,
            MaxDepth = JqBudget.MaximumDepth + 1
        };

        private readonly List<Frame> _frames = new();
        private JsonReaderState _state = new(Options);
        private int _offset;
        private JsonNode? _root;
        private bool _fallback;

        private readonly record struct Frame(JsonNode Container, string? Key);

        internal void Reset()
        {
            _state = new JsonReaderState(Options);
            _offset = 0;
            _root = null;
            _frames.Clear();
            _fallback = false;
        }

        internal bool TryRead(ReadOnlySpan<byte> source, bool isFinal, out JsonNode? value, out int consumed)
        {
            value = null;
            consumed = 0;
            if (!_fallback)
            {
                try
                {
                    var reader = new Utf8JsonReader(source[_offset..], isFinal, _state);
                    while (reader.Read())
                    {
                        budget.CheckCancellation();
                        switch (reader.TokenType)
                        {
                            case JsonTokenType.StartArray:
                            case JsonTokenType.StartObject:
                                if (_frames.Count >= JqBudget.MaximumDepth)
                                    throw new JqQuotaException("value nesting limit exceeded");
                                budget.ChargeNode();
                                if (_frames.Count == _frames.Capacity)
                                {
                                    int capacity = System.Math.Min(JqBudget.MaximumDepth,
                                        System.Math.Max(4, _frames.Capacity * 2));
                                    budget.ChargeBytes(32L + capacity * 2L * System.IntPtr.Size);
                                    _frames.Capacity = capacity;
                                }
                                JsonNode container = reader.TokenType == JsonTokenType.StartArray
                                    ? new JsonArray() : new JsonObject();
                                Attach(container);
                                _frames.Add(new Frame(container, null));
                                break;
                            case JsonTokenType.PropertyName:
                                Frame frame = _frames[^1];
                                _frames[^1] = frame with { Key = runtime.ReadBoundedString(ref reader) };
                                break;
                            case JsonTokenType.EndArray:
                            case JsonTokenType.EndObject:
                                _frames.RemoveAt(_frames.Count - 1);
                                break;
                            default:
                                Attach(runtime.ReadScalar(ref reader));
                                break;
                        }
                        if (_frames.Count == 0)
                        {
                            consumed = _offset + (int)reader.BytesConsumed;
                            budget.ChargeBytes(consumed);
                            value = _root;
                            Reset();
                            return true;
                        }
                    }
                    _offset += (int)reader.BytesConsumed;
                    _state = reader.CurrentState;
                    if (!isFinal)
                        return false;
                }
                catch (System.Exception exception) when (exception is JsonException or System.InvalidOperationException)
                {
                    // Quotas and cancellation never take the compatibility fallback.
                }
                _fallback = true;
                _root = null;
                _frames.Clear();
            }
            value = runtime.ReadJsonValue(source, out consumed);
            Reset();
            return true;

            void Attach(JsonNode? node)
            {
                if (_frames.Count == 0)
                    _root = node;
                else if (_frames[^1].Container is JsonArray array)
                    array.Add(node);
                else
                {
                    Frame frame = _frames[^1];
                    if (frame.Container is not JsonObject obj || frame.Key is not string key)
                        throw new System.InvalidOperationException("Expected a JSON object property.");
                    obj[key] = node;
                    _frames[^1] = frame with { Key = null };
                }
            }
        }
    }
}
