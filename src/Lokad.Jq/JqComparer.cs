using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lokad.Jq;

// One ordering operation reuses its scratch. Recursive object comparisons need
// separate key buffers at each live depth; no input or sorted-key cache survives.
internal sealed class JqComparer(JqBudget budget) : IComparer<JsonNode?>,
    IComparer<IReadOnlyList<JsonNode?>>, IDisposable
{
    private KeyStorage? _keys;

    public int Compare(JsonNode? left, JsonNode? right) => CompareValue(left, right, 0, budget, ref _keys);

    int IComparer<IReadOnlyList<JsonNode?>>.Compare(IReadOnlyList<JsonNode?>? left, IReadOnlyList<JsonNode?>? right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return CompareKeys(left, right);
    }

    internal int CompareKeys(IReadOnlyList<JsonNode?> left, IReadOnlyList<JsonNode?> right)
    {
        budget.CheckCancellation();
        int shared = Math.Min(left.Count, right.Count);
        for (int index = 0; index < shared; index++)
        {
            int order = Compare(left[index], right[index]);
            if (order != 0) return order;
        }
        return left.Count.CompareTo(right.Count);
    }

    internal IEnumerable<string> OrderKeys(JsonObject obj, int depth)
    {
        _keys ??= new KeyStorage(budget);
        KeyBuffers buffers = _keys.Get(depth, obj.Count, 0);
        try
        {
            int index = 0;
            foreach (var property in obj)
            {
                budget.CheckCancellation();
                buffers.Left[index++] = property.Key;
            }
            Sort(buffers.Left, obj.Count, _keys.StringOrder);
            for (index = 0; index < obj.Count; index++)
            {
                budget.CheckCancellation();
                string key = buffers.Left[index];
                yield return key;
            }
        }
        finally { Array.Clear(buffers.Left, 0, obj.Count); }
    }

    // Scalar/array comparisons allocate no owner unless object scratch is needed.
    internal static int CompareValues(JsonNode? left, JsonNode? right, JqBudget budget)
    {
        KeyStorage? keys = null;
        try { return CompareValue(left, right, 0, budget, ref keys); }
        finally { keys?.Dispose(); }
    }

    private static int CompareValue(JsonNode? left, JsonNode? right, int depth, JqBudget budget, ref KeyStorage? keys)
    {
        budget.CheckCancellation();
        JqRecursion.CheckValueDepth(left, depth);
        JqRecursion.CheckValueDepth(right, depth);
        int leftRank = Rank(left, out double leftNumber);
        int rightRank = Rank(right, out double rightNumber);
        if (leftRank != rightRank) return leftRank.CompareTo(rightRank);
        if (leftRank == 0) return (left is null ? 0 : 1).CompareTo(right is null ? 0 : 1);
        if (leftRank == 3) return leftNumber.CompareTo(rightNumber);
        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            int shared = Math.Min(leftArray.Count, rightArray.Count);
            for (int index = 0; index < shared; index++)
            {
                int order = CompareValue(leftArray[index], rightArray[index], depth + 1, budget, ref keys);
                if (order != 0) return order;
            }
            return leftArray.Count.CompareTo(rightArray.Count);
        }
        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            keys ??= new KeyStorage(budget);
            KeyBuffers buffers = keys.Get(depth, leftObject.Count, rightObject.Count);
            try
            {
                int index = 0;
                foreach (var property in leftObject)
                {
                    budget.CheckCancellation();
                    buffers.Left[index++] = property.Key;
                }
                index = 0;
                foreach (var property in rightObject)
                {
                    budget.CheckCancellation();
                    buffers.Right[index++] = property.Key;
                }
                Sort(buffers.Left, leftObject.Count, keys.StringOrder);
                Sort(buffers.Right, rightObject.Count, keys.StringOrder);
                int shared = Math.Min(leftObject.Count, rightObject.Count);
                for (index = 0; index < shared; index++)
                {
                    int order = CompareStrings(buffers.Left[index], buffers.Right[index], budget.CancellationToken);
                    if (order != 0) return order;
                }
                if (leftObject.Count != rightObject.Count) return leftObject.Count.CompareTo(rightObject.Count);
                for (index = 0; index < leftObject.Count; index++)
                {
                    string key = buffers.Left[index];
                    int order = CompareValue(leftObject[key], rightObject[key], depth + 1, budget, ref keys);
                    if (order != 0) return order;
                }
                return 0;
            }
            finally
            {
                Array.Clear(buffers.Left, 0, leftObject.Count);
                Array.Clear(buffers.Right, 0, rightObject.Count);
            }
        }
        if (leftRank != 4) return 0;
        // Rank has already established string kind. Avoid probing it again
        // when extracting ordinary scalar payloads; retain the general fallback.
        string? first = left is JsonValue firstValue && firstValue.TryGetValue<string>(out string? firstText)
            ? firstText : JqRuntime.String(left);
        string? second = right is JsonValue secondValue && secondValue.TryGetValue<string>(out string? secondText)
            ? secondText : JqRuntime.String(right);
        return CompareStrings(first, second, budget.CancellationToken);
    }

    private static int Rank(JsonNode? node, out double number)
    {
        number = 0;
        switch (node?.GetValueKind() ?? JsonValueKind.Null)
        {
            case JsonValueKind.Null: return 0;
            case JsonValueKind.False: return 1;
            case JsonValueKind.True: return 2;
            case JsonValueKind.Number:
                number = JqRuntime.Number(node);
                return double.IsNaN(number) ? 0 : 3;
            case JsonValueKind.String: return 4;
            case JsonValueKind.Array: return 5;
            default: return 6;
        }
    }

    internal static int CompareStrings(string? left, string? right, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(left, right)) return 0;
        if (left is null || right is null) return left is null ? right is null ? 0 : -1 : 1;
        ReadOnlySpan<char> first = left.AsSpan(), second = right.AsSpan();
        while (!first.IsEmpty && !second.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(4096, Math.Min(first.Length, second.Length));
            int common = first[..length].CommonPrefixLength(second[..length]);
            // A common UTF-16 prefix can end inside a surrogate pair. Leave its
            // high surrogate for scalar decoding, including malformed text.
            if (common != 0 && char.IsHighSurrogate(first[common - 1])) common--;
            first = first[common..];
            second = second[common..];
            if (first.IsEmpty || second.IsEmpty) break;
            if (common == length) continue;
            if (!char.IsSurrogate(first[0]) && !char.IsSurrogate(second[0]))
                return first[0].CompareTo(second[0]);
            Rune.DecodeFromUtf16(first, out Rune leftRune, out int leftLength);
            Rune.DecodeFromUtf16(second, out Rune rightRune, out int rightLength);
            if (leftRune.Value != rightRune.Value) return leftRune.Value.CompareTo(rightRune.Value);
            first = first[leftLength..];
            second = second[rightLength..];
        }
        return first.IsEmpty ? second.IsEmpty ? 0 : -1 : 1;
    }

    internal static IEnumerable<T> OrderBy<T, TKey>(IEnumerable<T> source, Func<T, TKey> key, IComparer<TKey> comparer)
    {
        using var iterator = source.OrderBy(key, comparer).GetEnumerator();
        while (true)
        {
            bool moved;
            try { moved = iterator.MoveNext(); }
            catch (InvalidOperationException exception) when (TerminalCause(exception) is { } cause)
            { ExceptionDispatchInfo.Capture(cause).Throw(); throw; }
            if (!moved) yield break;
            yield return iterator.Current;
        }
    }

    internal static async IAsyncEnumerable<T> OrderBy<T, TKey>(IAsyncEnumerable<T> source, Func<T, TKey> key, IComparer<TKey> comparer)
    {
        await using var iterator = source.OrderBy(key, comparer).ConfigureAwait(false).GetAsyncEnumerator();
        while (true)
        {
            bool moved;
            try { moved = await iterator.MoveNextAsync(); }
            catch (InvalidOperationException exception) when (TerminalCause(exception) is { } cause)
            { ExceptionDispatchInfo.Capture(cause).Throw(); throw; }
            if (!moved) yield break;
            yield return iterator.Current;
        }
    }

    private static void Sort<T>(T[] values, int count, IComparer<T> comparer)
    {
        try { Array.Sort(values, 0, count, comparer); }
        catch (InvalidOperationException exception) when (TerminalCause(exception) is { } cause)
        { ExceptionDispatchInfo.Capture(cause).Throw(); throw; }
    }

    private static Exception? TerminalCause(Exception exception)
    {
        while (exception is InvalidOperationException { InnerException: { } inner }) exception = inner;
        return exception is JqQuotaException or OperationCanceledException or JqHostFailureException or OutOfMemoryException
            ? exception : null;
    }

    private sealed class KeyBuffers
    {
        internal string[] Left = [];
        internal string[] Right = [];
    }

    private sealed class KeyStorage(JqBudget budget) : IDisposable
    {
        private readonly List<KeyBuffers> _levels = [];
        private readonly JqMemoryReservation _memory = new(budget.MemoryBudget);
        internal IComparer<string> StringOrder { get; } = Comparer<string>.Create(
            (left, right) => CompareStrings(left, right, budget.CancellationToken));

        internal KeyBuffers Get(int depth, int leftCount, int rightCount)
        {
            while (_levels.Count <= depth) _levels.Add(new KeyBuffers());
            KeyBuffers buffers = _levels[depth];
            Grow(ref buffers.Left, leftCount);
            Grow(ref buffers.Right, rightCount);
            return buffers;
        }

        private void Grow(ref string[] buffer, int count)
        {
            if (count <= buffer.Length) return;
            budget.CheckCancellation();
            int capacity = JqBudget.BufferCapacity(count, 2L * buffer.Length);
            long previous = _memory.LastAdmittedBytes;
            _memory.Resize(checked(previous + (long)capacity * IntPtr.Size));
            budget.CheckCancellation();
            int oldLength = buffer.Length;
            try { buffer = new string[capacity]; }
            catch { _memory.Resize(previous); throw; }
            if (oldLength != 0) _memory.Resize(previous + (long)(capacity - oldLength) * IntPtr.Size);
        }

        public void Dispose() { _levels.Clear(); _memory.Dispose(); }
    }

    public void Dispose() => _keys?.Dispose();
}
