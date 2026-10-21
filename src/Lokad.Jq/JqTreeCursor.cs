using System.Text.Json.Nodes;

namespace Lokad.Jq;

// Open ancestors only. Capacity covers slots, object enumerators and cached path
// segments; siblings borrow the input and never need an inventory of their own.
internal sealed class JqTreeCursor(JqBudget budget) : IDisposable
{
    private readonly JqMemoryReservation _memory = new(budget.MemoryBudget);
    private readonly List<Frame> _frames = [];

    internal int Count => _frames.Count;

    internal void Push(JsonNode container)
    {
        budget.CheckCancellation();
        if (_frames.Count == _frames.Capacity)
        {
            int capacity = JqBudget.BufferCapacity((long)_frames.Count + 1, Math.Max(4, 2L * _frames.Capacity));
            long previous = _memory.LastAdmittedBytes;
            _memory.Resize(checked(previous + 128L * capacity));
            _frames.Capacity = capacity;
            if (previous != 0) _memory.Resize(128L * capacity);
        }
        _frames.Add(new Frame(container));
    }

    internal bool TryNext(out JsonNode? child)
    {
        budget.CheckCancellation();
        Frame frame = _frames[^1];
        bool found = frame.TryNext(out child);
        _frames[^1] = frame;
        return found;
    }

    internal void Pop()
    {
        _frames[^1].Dispose();
        _frames.RemoveAt(_frames.Count - 1);
    }

    internal IReadOnlyList<JqValueSegment> CopySegments(IReadOnlyList<JqValueSegment> prefix)
    {
        budget.ReserveBytes(128 + 16L * (prefix.Count + _frames.Count));
        var path = new List<JqValueSegment>(checked(prefix.Count + _frames.Count));
        foreach (JqValueSegment segment in prefix)
        {
            budget.CheckCancellation();
            path.Add(segment);
        }
        for (int index = 0; index < _frames.Count; index++)
        {
            budget.CheckCancellation();
            Frame frame = _frames[index];
            path.Add(frame.Segment);
            _frames[index] = frame;
        }
        return path;
    }

    public void Dispose()
    {
        while (_frames.Count != 0) Pop();
        _memory.Dispose();
    }

    private struct Frame : IDisposable
    {
        private readonly JsonNode _container;
        private readonly IEnumerator<KeyValuePair<string, JsonNode?>>? _properties;
        private int _index;
        private JqValueSegment? _segment;

        internal Frame(JsonNode container)
        {
            _container = container;
            _properties = container is JsonObject obj ? obj.GetEnumerator() : null;
            _index = -1;
            _segment = null;
        }

        internal JqValueSegment Segment => _segment ??= _properties is { } properties
            ? new KeySegment(properties.Current.Key) : new IndexSegment(_index, false);

        internal bool TryNext(out JsonNode? child)
        {
            _segment = null;
            if (_container is JsonArray array && ++_index < array.Count)
            {
                child = array[_index];
                return true;
            }
            if (_properties is not null && _properties.MoveNext())
            {
                child = _properties.Current.Value;
                return true;
            }
            child = null;
            return false;
        }

        public void Dispose() => _properties?.Dispose();
    }
}
