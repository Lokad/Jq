using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Lokad.Jq;

/// <summary>
/// Conservative, cumulative allocation allowances for one execution. No refunds: intermediate
/// values, clones and discarded results consume the same budget as retained values.
/// These limits bound jq work, not the process's total managed heap.
/// </summary>
internal sealed class JqBudget(CancellationToken cancellationToken)
{
    internal const int MaximumStringLength = 8 * 1024 * 1024;
    internal const int MaximumInputBytes = 16 * 1024 * 1024;
    internal const int MaximumJsonBytes = 32 * 1024 * 1024;
    internal const int MaximumJsonBufferBytes = 64 * 1024 * 1024;
    internal const int MaximumDepth = 64;
    private long _remainingBytes = 256 * 1024 * 1024;
    private int _remainingNodes = 262144;
    private int _remainingInput = MaximumInputBytes;
    private int _remainingOutput = MaximumJsonBytes;
    private int _evaluationDepth;

    internal CancellationToken CancellationToken => cancellationToken;

    internal void CheckCancellation() => cancellationToken.ThrowIfCancellationRequested();

    internal void ChargeBytes(long count)
    {
        CheckCancellation();
        if (count < 0 || count > _remainingBytes)
            throw new JqQuotaException("memory budget exceeded");
        _remainingBytes -= count;
    }

    internal void ChargeNode()
    {
        CheckCancellation();
        if (_remainingNodes-- <= 0)
            throw new JqQuotaException("value budget exceeded");
    }

    internal void ChargeInput(int bytes)
    {
        if (bytes > _remainingInput)
            throw new JqQuotaException("input exceeds the 16 MiB limit");
        _remainingInput -= bytes;
        // Allow for the old and new backing arrays while the bounded input buffer grows.
        ChargeBytes(4L * bytes);
    }

    internal void ChargeOutput(int bytes)
    {
        if (bytes > _remainingOutput)
            throw new JqQuotaException("output exceeds the 32 MiB limit");
        _remainingOutput -= bytes;
        ChargeBytes(bytes);
    }

    internal static void CheckStringLength(long length)
    {
        if (length > MaximumStringLength)
            throw new JqQuotaException("string result exceeds the 16 MiB UTF-16 limit");
    }

    internal void ChargeString(long length)
    {
        CheckStringLength(length);
        ChargeBytes(length * sizeof(char));
    }

    internal void Append(StringBuilder builder, ReadOnlySpan<char> text)
    {
        CheckStringLength((long)builder.Length + text.Length);
        ChargeString(text.Length);
        builder.Append(text);
    }

    internal string Finish(StringBuilder builder)
    {
        ChargeString(builder.Length);
        return builder.ToString();
    }

    internal void ChargeTree(JsonNode? node)
    {
        Visit(node, 0);

        void Visit(JsonNode? value, int depth)
        {
            if (depth > MaximumDepth || depth == MaximumDepth && value is JsonArray or JsonObject)
                throw new JqQuotaException("value nesting limit exceeded");
            ChargeNode();
            switch (value)
            {
                case JsonArray array:
                    foreach (var item in array) Visit(item, depth + 1);
                    break;
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        ChargeString(property.Key.Length);
                        Visit(property.Value, depth + 1);
                    }
                    break;
                case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                    ChargeString(text.Length);
                    break;
            }
        }
    }

    internal void EnterEvaluation()
    {
        ChargeNode();
        if (_evaluationDepth >= 256)
            throw new JqQuotaException("filter nesting limit exceeded");
        _evaluationDepth++;
    }

    internal void LeaveEvaluation() => _evaluationDepth--;
}
