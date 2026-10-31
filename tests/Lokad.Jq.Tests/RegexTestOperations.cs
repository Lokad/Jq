namespace Lokad.Jq.Tests;

// Direct cache tests use the same operation/retention boundaries as runtime callers.
internal static class RegexTestOperations
{
    internal static bool IsMatch(JqRegexCache cache, JqRegexCache.Pattern pattern,
        ReadOnlyMemory<byte> bytes, JqRegexMatchOptions options)
    {
        using var subject = cache.Prepare(pattern, bytes, null);
        return subject.IsMatch(options, CancellationToken.None);
    }

    internal static JqRegexMatch Match(JqRegexCache cache, JqRegexCache.Pattern pattern,
        ReadOnlyMemory<byte> bytes, int start, JqRegexMatchOptions options)
    {
        if (start != 0) throw new InvalidOperationException("Use a cursor for continued searches.");
        using var subject = cache.Prepare(pattern, bytes, null);
        using var cursor = subject.CreateCursor(options, CancellationToken.None);
        return cursor.MoveNext(CancellationToken.None) ? cursor.RetainCurrent(CancellationToken.None) : default;
    }
}
