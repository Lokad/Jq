using System.Globalization;

namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(64)]
    public void Jq_NativeObjectLookupPreservesOrderAndLastValue(int width)
    {
        using var budget = new JqBudget(CancellationToken.None);
        var value = new JqObject(budget);
        string[] names = Enumerable.Range(0, width).Select(i => "κ" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        for (int index = 0; index < width; index++)
            value[JqString.FromHost(names[index], budget)] = JqScalar.Create(index);
        for (int index = 0; index < width; index++)
        {
            Assert.True(value.TryGetPropertyValue(names[index], out JqValue? item));
            Assert.Equal(index, item?.GetValue<int>());
            JqString key = JqString.FromHost(names[index], budget);
            Assert.True(value.ContainsKey(key));
            Assert.Same(item, value[key]);
            value[key] = JqScalar.Create(-index);
            Assert.Equal(-index, value[names[index]]?.GetValue<int>());
        }
        Assert.Equal(names, value.Select(item => item.Key.GetValue<string>()));
        Assert.False(value.ContainsKey("missing"));
        Assert.False(value.TryGetPropertyValue(JqString.FromHost("missing", budget), out JqValue? absent));
        Assert.Null(absent);
        Assert.Equal(width, value.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(9)]
    public void Jq_NativeObjectReadsAndWritesObserveCancellation(int width)
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new JqBudget(cancellation.Token);
        var value = new JqObject(budget);
        for (int index = 0; index < width; index++) value[index.ToString(CultureInfo.InvariantCulture)] = null;
        JqString key = JqString.FromHost("0", budget);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => value.TryGetPropertyValue("0", out _));
        Assert.Throws<OperationCanceledException>(() => value.TryGetPropertyValue(key, out _));
        Assert.Throws<OperationCanceledException>(() => value[key] = null);
        Assert.Throws<OperationCanceledException>(() => value["0"] = null);
        Assert.Equal(width, value.Count);
    }
}
