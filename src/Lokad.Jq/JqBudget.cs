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
internal sealed class JqBudget
{
    internal const int MaximumStringLength = 8 * 1024 * 1024;
    internal const int MaximumInputBytes = 16 * 1024 * 1024;
    internal const int MaximumJsonBytes = 32 * 1024 * 1024;
    internal const int MaximumJsonBufferBytes = 64 * 1024 * 1024;
    internal const int MaximumDepth = 64;
    private readonly CancellationToken _cancellationToken;
    private long _remainingBytes;
    private int _remainingNodes;
    private int _remainingInput;
    private int _remainingOutput;
    private int _evaluationDepth;

    internal JqBudget(CancellationToken cancellationToken)
        : this(JqExecutionPolicy.Default, cancellationToken) { }

    internal JqBudget(JqExecutionPolicy policy, CancellationToken cancellationToken)
    {
        Policy = policy;
        _cancellationToken = cancellationToken;
        _remainingBytes = policy.MaximumAllocationBytes;
        _remainingNodes = policy.MaximumValueNodes;
        _remainingInput = policy.MaximumInputBytes;
        _remainingOutput = policy.MaximumOutputBytes;
    }

    internal JqExecutionPolicy Policy { get; }

    internal CancellationToken CancellationToken => _cancellationToken;

    internal int RemainingInput => _remainingInput;

    internal void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();

    internal string InputLimitMessage => Policy.MaximumInputBytes == MaximumInputBytes
        ? "input exceeds the 16 MiB limit"
        : $"input exceeds the {Policy.MaximumInputBytes}-byte limit";

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
        if (_remainingNodes <= 0)
            throw new JqQuotaException("value budget exceeded");
        _remainingNodes--;
    }

    internal void ChargeInput(int bytes)
    {
        CheckCancellation();
        if (bytes < 0 || bytes > _remainingInput)
            throw new JqQuotaException(InputLimitMessage);
        // Allow for the old and new backing arrays while the bounded input buffer grows.
        ChargeBytes(4L * bytes);
        _remainingInput -= bytes;
    }

    internal void ChargeOutput(int bytes)
    {
        CheckCancellation();
        if (bytes < 0 || bytes > _remainingOutput)
            throw new JqQuotaException(Policy.MaximumOutputBytes == MaximumJsonBytes
                ? "output exceeds the 32 MiB limit"
                : $"output exceeds the {Policy.MaximumOutputBytes}-byte limit");
        ChargeBytes(bytes);
        _remainingOutput -= bytes;
    }

    internal void CheckStringLength(long length)
    {
        CheckCancellation();
        if (length < 0 || length > Policy.MaximumStringLength)
            throw new JqQuotaException(Policy.MaximumStringLength == MaximumStringLength
                ? "string result exceeds the 16 MiB UTF-16 limit"
                : $"string result exceeds the {Policy.MaximumStringLength} UTF-16 code unit limit");
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
    }

    // Object construction keeps borrowed fields until a completed result is
    // copied. Account for the old prefix copy at the same depth and stage.
    internal void ChargeObjectPrefix(Dictionary<string, JsonNode?> fields)
    {
        ChargeNode();
        foreach (var field in fields)
        {
            ChargeString(field.Key.Length);
            Visit(field.Value, 1);
        }
    }

    private void Visit(JsonNode? value, int depth)
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

    internal void EnterEvaluation()
    {
        ChargeNode();
        if (_evaluationDepth >= 256)
            throw new JqQuotaException("filter nesting limit exceeded");
        _evaluationDepth++;
    }

    internal void LeaveEvaluation() => _evaluationDepth--;
}
