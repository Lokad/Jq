using System;
using System.Collections.Generic;

namespace Lokad.Jq;

/// <summary>Caches adapted regex patterns within one jq execution.</summary>
internal sealed class JqRegexCache : IDisposable
{
    private readonly JqBudget _budget;
    private bool _disposed;
    private Pattern? _idlePattern;
    private Pattern? _olderIdlePattern;
    private readonly Dictionary<PatternKey, Pattern> _patterns = new();

    internal JqRegexCache(JqBudget budget)
    {
        _budget = budget;
    }

    internal JqMemoryValue<Pattern> Rent(ReadOnlyMemory<byte> pattern, JqRegexCompileOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        IJqRegexEngine regexEngine = _budget.RegexEngine
            ?? throw new JqException("regex engine is not configured");
        var hash = new HashCode();
        hash.Add(options);
        for (int offset = 0; offset < pattern.Length;)
        {
            _budget.CheckCancellation();
            int length = Math.Min(4096, pattern.Length - offset);
            hash.AddBytes(pattern.Span.Slice(offset, length));
            offset += length;
        }
        int contentHash = hash.ToHashCode();
        var lookup = new PatternKey(pattern, options, contentHash);
        if (_patterns.TryGetValue(lookup, out Pattern? cached))
        {
            _budget.CheckCancellation();
            var lease = new JqMemoryValue<Pattern>(cached, cached);
            if (ReferenceEquals(cached, _idlePattern))
            {
                _idlePattern = _olderIdlePattern;
                _olderIdlePattern = null;
            }
            else if (ReferenceEquals(cached, _olderIdlePattern)) _olderIdlePattern = null;
            cached.Borrowers++;
            return lease;
        }
        RetireOlderIdlePattern();
        IJqCompiledRegex? compiled = null;
        var keys = new JqMemoryReservation(_budget.MemoryBudget);
        bool retained = false;
        try
        {
            // Own the persistent key before giving it to a borrowed engine. The
            // provider may borrow the bytes; record-local input can expire.
            // The key owner follows the compiled handle through all its borrowers.
            keys.Resize(128L + pattern.Length);
            _budget.CheckCancellation();
            ReadOnlyMemory<byte> ownedPattern = pattern.ToArray();
            try
            {
                if (!regexEngine.TryCompile(ownedPattern, options, _budget.MemoryBudget,
                    _budget.CancellationToken, out compiled, out string? error))
                    throw new JqException(error ?? "unsupported regex");
            }
            catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
            { throw new JqHostFailureException("regex compilation violated its contract", exception); }
            _budget.CheckCancellation();
            var ownedKey = new PatternKey(ownedPattern, options, contentHash);
            cached = new Pattern(this, ownedKey, compiled, keys);
            var lease = new JqMemoryValue<Pattern>(cached, cached);
            _patterns.Add(ownedKey, cached);
            cached.Borrowers++;
            retained = true;
            return lease;
        }
        catch (JqRegexResourceException exception) { throw Terminal(exception); }
        finally
        {
            if (!retained)
            {
                compiled?.Dispose();
                keys.Dispose();
            }
        }
    }

    internal JqRegexMatch Match(Pattern pattern, ReadOnlyMemory<byte> input,
        int start, JqRegexMatchOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        JqRegexMatch match = default;
        bool retained = false;
        try
        {
            try { match = pattern.Compiled.Match(input.Span, start, options, _budget.CancellationToken); }
            catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
            { throw new JqHostFailureException("regex matching violated its contract", exception); }
            _budget.CheckCancellation();
            var lifetime = new MatchLifetime(pattern, match.Owner);
            pattern.Borrowers++;
            retained = true;
            return new JqRegexMatch(match.Captures, lifetime);
        }
        catch (JqRegexResourceException exception) { throw Terminal(exception); }
        catch (NotSupportedException exception)
        {
            throw new JqException("unsupported regex: " + exception.Message);
        }
        finally { if (!retained) match.Dispose(); }
    }

    internal bool IsMatch(Pattern pattern, ReadOnlyMemory<byte> input, JqRegexMatchOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        bool matched;
        try { matched = pattern.Compiled.IsMatch(input.Span, options, _budget.CancellationToken); }
        catch (JqRegexResourceException exception) { throw Terminal(exception); }
        catch (NotSupportedException ex) { throw new JqException($"unsupported regex: {ex.Message}"); }
        catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
        { throw new JqHostFailureException("regex matching violated its contract", exception); }
        _budget.CheckCancellation();
        return matched;
    }

    internal static IReadOnlyDictionary<int, string> GetGroupNames(Pattern pattern)
    {
        try { return pattern.Compiled.GroupNames; }
        catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
        { throw new JqHostFailureException("regex metadata violated its contract", exception); }
    }

    private static JqQuotaException Terminal(JqRegexResourceException exception) =>
        new(exception.IsMemoryAdmission ? "memory budget exceeded" : exception.Message)
        { IsMemoryAdmission = exception.IsMemoryAdmission };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RetireOlderIdlePattern();
        if (_idlePattern is { } idle)
        {
            _idlePattern = null;
            Retire(idle);
        }
    }

    private void Return(Pattern pattern)
    {
        if (--pattern.Borrowers != 0) return;
        if (_disposed) Retire(pattern);
        else
        {
            RetireOlderIdlePattern();
            _olderIdlePattern = _idlePattern;
            _idlePattern = pattern;
        }
    }

    private void RetireOlderIdlePattern()
    {
        if (_olderIdlePattern is not { } idle) return;
        _olderIdlePattern = null;
        Retire(idle);
    }

    private void Retire(Pattern pattern)
    {
        _patterns.Remove(pattern.Key);
        pattern.Compiled.Dispose();
        pattern.Keys.Dispose();
    }

    internal readonly struct PatternKey(ReadOnlyMemory<byte> utf8, JqRegexCompileOptions options, int hash) : IEquatable<PatternKey>
    {
        private ReadOnlyMemory<byte> Utf8 { get; } = utf8;
        private JqRegexCompileOptions Options { get; } = options;
        public bool Equals(PatternKey other) => Options == other.Options && Utf8.Span.SequenceEqual(other.Utf8.Span);
        public override bool Equals(object? other) => other is PatternKey key && Equals(key);
        public override int GetHashCode() => hash;
    }

    // Each operation and result holds its own borrow. Only the last
    // two returned idle patterns are reusable; active entries are never evicted.
    internal sealed class Pattern(JqRegexCache cache, PatternKey key,
        IJqCompiledRegex compiled, JqMemoryReservation keys) : IDisposable
    {
        internal PatternKey Key { get; } = key;
        internal IJqCompiledRegex Compiled { get; } = compiled;
        internal JqMemoryReservation Keys { get; } = keys;
        internal int Borrowers { get; set; }
        public void Dispose() => cache.Return(this);
    }

    private sealed class MatchLifetime(Pattern pattern, IDisposable? owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { owner?.Dispose(); }
            finally { pattern.Dispose(); }
        }
    }
}
