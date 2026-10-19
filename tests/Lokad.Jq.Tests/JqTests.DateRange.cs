namespace Lokad.Jq.Tests;

public sealed partial class JqTests
{
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MinValue + 1)]
    [InlineData(-4000000000000000000L)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(4000000000000000000L)]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue)]
    public void Jq_UtcCivilAssemblyRoundTripsTheFullEpochDomain(long epoch)
    {
        var wall = JqTime.FromEpochUtc(epoch, 0);
        Assert.Equal(epoch, JqTime.ToEpochSeconds(wall.Year, wall.Month - 1,
            wall.Day, wall.Hour, wall.Minute, (long)wall.Second));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Jq_UtcCivilAssemblyAcceptsNormalizedExtremeSeconds(long second)
    {
        Assert.Equal(second, JqTime.ToEpochSeconds(1970, 0, 1, 0, 0, second));
    }

    [Theory]
    [InlineData(1970L, 0L, 1L, 0L, 1L, long.MaxValue)]
    [InlineData(1970L, 0L, 1L, 0L, -1L, long.MinValue)]
    [InlineData(1970L, long.MaxValue, 1L, 0L, 0L, 0L)]
    [InlineData(1970L, 0L, long.MinValue, 0L, 0L, 0L)]
    [InlineData(1970L, 0L, 1L, long.MaxValue, 0L, 0L)]
    [InlineData(long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue)]
    public void Jq_UtcCivilAssemblyRejectsActualEpochOverflow(long year, long month,
        long day, long hour, long minute, long second)
    {
        var error = Assert.Throws<JqException>(() => JqTime.ToEpochSeconds(year, month, day, hour, minute, second));
        Assert.Equal("invalid gmtime representation", error.Message);
    }
}
