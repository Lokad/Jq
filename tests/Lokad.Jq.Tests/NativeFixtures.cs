namespace Lokad.Jq.Tests;

// Unmetered setup values are independent of the operation budget under test.
internal static class NativeFixtures
{
    internal static JqBudget Budget() => new(CancellationToken.None);
    internal static JqScalar Create(int value) => JqScalar.Create(value);
    internal static JqScalar Create(long value) => JqScalar.Create(value);
    internal static JqScalar Create(double value) => JqScalar.Create(value);
    internal static JqScalar Create(decimal value) => JqScalar.Create(value);
    internal static JqScalar Create(bool value) => JqScalar.Create(value);
    internal static JqString Create(string value)
    {
        using var budget = Budget();
        return JqString.FromHost(value, budget);
    }
    internal static JqString Create(JqString value) => JqScalar.Create(value);
    internal static JqObject Object(IEnumerable<KeyValuePair<string, JqValue?>> properties)
    {
        var result = new JqObject(Budget());
        foreach (var property in properties) result.Add(property.Key, property.Value);
        return result;
    }
    internal static T GetValue<T>(this JqValue value)
    {
        if (typeof(T) == typeof(string) && value is JqString native)
        {
            using var budget = Budget();
            string text = native.ToHost(budget);
            return System.Runtime.CompilerServices.Unsafe.As<string, T>(ref text);
        }
        return value is JqScalar scalar && scalar.TryGetValue<T>(out T? result)
            ? result : throw new InvalidOperationException("Unexpected scalar representation.");
    }
    internal static JqValue? Parse(string json)
    {
        var budget = Budget();
        return new JqRuntime(budget).ParseJson(json);
    }
    internal static JqValue? DeepClone(this JqValue? value)
    {
        using var budget = Budget();
        return budget.CopyValue(value, 0);
    }
    internal static bool DeepEquals(JqValue? left, JqValue? right)
    {
        using var budget = Budget();
        return new JqRuntime(budget).JsonEquals(left, right);
    }
    internal static string ToJsonString(this JqValue value)
    {
        using var budget = Budget();
        return new JqRuntime(budget).Serialize(value, false, null, false);
    }
}
