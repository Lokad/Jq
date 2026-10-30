namespace Lokad.Jq;

// One lazy index per regex operation. Sparse scalar-boundary checkpoints let
// overlapping captures and lookbehind revisit earlier positions without walking
// the complete subject for every capture. ASCII prefixes need no index storage.
internal sealed class JqRegexCoordinates(ReadOnlyMemory<byte> subject, JqBudget budget) : IDisposable
{
    private const int Stride = 256;
    private readonly JqMemoryReservation _memory = new(budget.MemoryBudget);
    private readonly List<Position> _checkpoints = [];
    private Position _furthest;
    private int _asciiPrefix;

    internal readonly record struct Position(int Byte, int Scalar);

    internal Position At(int target)
    {
        budget.CheckCancellation();
        if ((uint)target > (uint)subject.Length)
            throw new JqHostFailureException("regex capture is outside its subject", null);
        if (target <= _asciiPrefix) return new Position(target, target);
        Position position;
        if (target >= _furthest.Byte)
        {
            position = _furthest;
            Advance(ref position, target, true);
            _furthest = position;
        }
        else
        {
            int low = 0, high = _checkpoints.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_checkpoints[middle].Byte <= target) low = middle + 1;
                else high = middle;
            }
            position = low == 0 ? new Position(_asciiPrefix, _asciiPrefix) : _checkpoints[low - 1];
            Advance(ref position, target, false);
        }
        return position;
    }

    internal int AfterEmpty(int offset) => offset + Width(subject.Span[offset]);

    private void Advance(ref Position position, int target, bool extend)
    {
        ReadOnlySpan<byte> bytes = subject.Span;
        while (position.Byte < target)
        {
            if ((position.Scalar & 4095) == 0) budget.CheckCancellation();
            byte first = bytes[position.Byte];
            int width = Width(first);
            if (position.Byte + width > target || (first & 0xc0) == 0x80)
                throw new JqHostFailureException("regex capture splits a UTF-8 scalar", null);
            position = new Position(position.Byte + width, position.Scalar + 1);
            if (extend)
            {
                if (first < 128 && _asciiPrefix == position.Byte - 1) _asciiPrefix = position.Byte;
                int indexed = _checkpoints.Count == 0 ? _asciiPrefix : _checkpoints[^1].Byte;
                if (position.Byte - indexed >= Stride) Add(position);
            }
        }
    }

    private static int Width(byte first) => first < 0x80 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;

    private void Add(Position position)
    {
        if (_checkpoints.Count == _checkpoints.Capacity)
        {
            int previous = _checkpoints.Capacity;
            int capacity = JqBudget.BufferCapacity((long)_checkpoints.Count + 1, Math.Max(4, 2L * previous));
            _memory.Resize(64L + 8L * (previous + capacity));
            budget.CheckCancellation();
            _checkpoints.EnsureCapacity(capacity);
            if (previous != 0) _memory.Resize(64L + 8L * capacity);
        }
        _checkpoints.Add(position);
    }

    public void Dispose() => _memory.Dispose();
}
