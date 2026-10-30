using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Lokad.Jq;

// JSON null is represented by a null reference. Containers have no parent link;
// updates retain the evaluator's explicit copy-before-mutation boundaries.
internal abstract class JqValue
{
    internal abstract JsonValueKind GetValueKind();
    internal virtual JqValue? this[int index]
    {
        get => throw new InvalidOperationException("Expected an array.");
        set => throw new InvalidOperationException("Expected an array.");
    }
    internal virtual JqValue? this[string key]
    {
        get => throw new InvalidOperationException("Expected an object.");
        set => throw new InvalidOperationException("Expected an object.");
    }

    public static implicit operator JqValue(int value) => JqScalar.Create(value);
    public static implicit operator JqValue(long value) => JqScalar.Create(value);
    public static implicit operator JqValue(double value) => JqScalar.Create(value);
    public static implicit operator JqValue(bool value) => JqScalar.Create(value);
}

internal abstract class JqScalar : JqValue
{
    internal abstract bool TryGetValue<T>([MaybeNullWhen(false)] out T value);
    internal abstract JqScalar Copy();
    internal abstract void WriteTo(Utf8JsonWriter writer);

    internal static JqScalar Create(int value) => new Number<int>(value);
    internal static JqScalar Create(long value) => new Number<long>(value);
    internal static JqScalar Create(double value) => new Number<double>(value);
    internal static JqScalar Create(decimal value) => new Number<decimal>(value);
    internal static JqScalar Create(bool value) => new Boolean(value);
    internal static JqString Create(string value, JqBudget budget) => JqString.FromHost(value, budget);
    internal static JqString Create(JqString value) => (JqString)value.Copy();

    private sealed class Number<TNumber>(TNumber number) : JqScalar where TNumber : struct
    {
        internal override JsonValueKind GetValueKind() => JsonValueKind.Number;
        internal override bool TryGetValue<T>([MaybeNullWhen(false)] out T value)
        {
            if (typeof(T) == typeof(TNumber))
            {
                TNumber copy = number;
                value = Unsafe.As<TNumber, T>(ref copy);
                return true;
            }
            value = default;
            return false;
        }
        internal override JqScalar Copy() => new Number<TNumber>(number);
        internal override void WriteTo(Utf8JsonWriter writer)
        {
            TNumber copy = number;
            if (typeof(TNumber) == typeof(int)) writer.WriteNumberValue(Unsafe.As<TNumber, int>(ref copy));
            else if (typeof(TNumber) == typeof(long)) writer.WriteNumberValue(Unsafe.As<TNumber, long>(ref copy));
            else if (typeof(TNumber) == typeof(double)) writer.WriteNumberValue(Unsafe.As<TNumber, double>(ref copy));
            else if (typeof(TNumber) == typeof(decimal)) writer.WriteNumberValue(Unsafe.As<TNumber, decimal>(ref copy));
            else throw new InvalidOperationException("Unsupported number representation.");
        }
    }

    private sealed class Boolean(bool boolean) : JqScalar
    {
        internal override JsonValueKind GetValueKind() => boolean ? JsonValueKind.True : JsonValueKind.False;
        internal override bool TryGetValue<T>([MaybeNullWhen(false)] out T value)
        {
            if (typeof(T) == typeof(bool))
            {
                bool copy = boolean;
                value = Unsafe.As<bool, T>(ref copy);
                return true;
            }
            value = default;
            return false;
        }
        internal override JqScalar Copy() => new Boolean(boolean);
        internal override void WriteTo(Utf8JsonWriter writer) => writer.WriteBooleanValue(boolean);
    }
}

internal sealed class JqArray : JqValue, IReadOnlyList<JqValue?>, IList<JqValue?>
{
    private readonly List<JqValue?> _items;
    internal JqArray() => _items = [];
    internal JqArray(params JqValue?[] values) => _items = new List<JqValue?>(values);
    public int Count => _items.Count;
    internal override JqValue? this[int index] { get => _items[index]; set => _items[index] = value; }
    JqValue? IReadOnlyList<JqValue?>.this[int index] => _items[index];
    internal void Add(JqValue? value) => _items.Add(value);
    internal void RemoveAt(int index) => _items.RemoveAt(index);
    internal void Clear() => _items.Clear();
    bool ICollection<JqValue?>.IsReadOnly => false;
    JqValue? IList<JqValue?>.this[int index] { get => _items[index]; set => _items[index] = value; }
    void ICollection<JqValue?>.Add(JqValue? value) => Add(value);
    void ICollection<JqValue?>.Clear() => Clear();
    bool ICollection<JqValue?>.Contains(JqValue? value) => _items.Contains(value);
    void ICollection<JqValue?>.CopyTo(JqValue?[] destination, int index) => _items.CopyTo(destination, index);
    bool ICollection<JqValue?>.Remove(JqValue? value) => _items.Remove(value);
    int IList<JqValue?>.IndexOf(JqValue? value) => _items.IndexOf(value);
    void IList<JqValue?>.Insert(int index, JqValue? value) => _items.Insert(index, value);
    void IList<JqValue?>.RemoveAt(int index) => RemoveAt(index);
    public IEnumerator<JqValue?> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal override JsonValueKind GetValueKind() => JsonValueKind.Array;
}

internal sealed class JqObject : JqValue, IEnumerable<KeyValuePair<JqString, JqValue?>>
{
    private readonly List<KeyValuePair<JqString, JqValue?>> _items = [];
    private readonly Dictionary<JqString, int> _indices;
    private readonly JqBudget _budget;

    internal JqObject(JqBudget budget)
    {
        _budget = budget;
        _indices = new Dictionary<JqString, int>(budget.StringKeys);
    }

    internal int Count => _items.Count;
    internal KeyValuePair<JqString, JqValue?> GetAt(int index) => _items[index];
    internal override JsonValueKind GetValueKind() => JsonValueKind.Object;
    internal override JqValue? this[string key]
    {
        get => TryGetPropertyValue(key, out JqValue? value) ? value : null;
        set
        {
            var lookup = _indices.GetAlternateLookup<string>();
            if (lookup.TryGetValue(key, out int index))
                _items[index] = new(_items[index].Key, value);
            else Add(JqString.FromHost(key, _budget), value);
        }
    }
    internal JqValue? this[JqString key]
    {
        get => TryGetPropertyValue(key, out JqValue? value) ? value : null;
        set
        {
            if (_indices.TryGetValue(key, out int index)) _items[index] = new(_items[index].Key, value);
            else Add(key, value);
        }
    }

    internal void Add(JqString key, JqValue? value)
    {
        _indices.Add(key, _items.Count);
        _items.Add(new(key, value));
    }
    internal void Add(string key, JqValue? value) => Add(JqString.FromHost(key, _budget), value);
    internal bool ContainsKey(string key) => _indices.GetAlternateLookup<string>().ContainsKey(key);
    internal bool ContainsKey(JqString key) => _indices.ContainsKey(key);
    internal bool TryGetPropertyValue(string key, out JqValue? value)
    {
        bool found = _indices.GetAlternateLookup<string>().TryGetValue(key, out int index);
        value = found ? _items[index].Value : null;
        return found;
    }
    internal bool TryGetPropertyValue(JqString key, out JqValue? value)
    {
        bool found = _indices.TryGetValue(key, out int index);
        value = found ? _items[index].Value : null;
        return found;
    }
    public List<KeyValuePair<JqString, JqValue?>>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<KeyValuePair<JqString, JqValue?>> IEnumerable<KeyValuePair<JqString, JqValue?>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class JqStringKeys(JqBudget budget) : IEqualityComparer<JqString>, IAlternateEqualityComparer<string, JqString>
{
    public bool Equals(JqString? left, JqString? right) => ReferenceEquals(left, right)
        || left is not null && right is not null && left.ContentEquals(right, budget.CancellationToken);
    public int GetHashCode(JqString value) => value.ContentHashCode(budget.CancellationToken);
    public JqString Create(string value) => JqString.FromHost(value, budget);
    public bool Equals(string left, JqString right) => right.ContentEqualsHost(left, budget.CancellationToken);
    public int GetHashCode(string value)
    {
        var hash = new HashCode();
        foreach (Rune rune in value.EnumerateRunes())
        {
            budget.CheckCancellation();
            hash.Add(rune.Value);
        }
        return hash.ToHashCode();
    }
}
