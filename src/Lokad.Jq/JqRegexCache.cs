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
    private readonly Dictionary<(string Pattern, JqRegexCompileOptions Options), Pattern> _patterns = new();

    internal JqRegexCache(JqBudget budget)
    {
        _budget = budget;
    }

    internal JqMemoryValue<Pattern> Rent(string pattern, JqRegexCompileOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        IJqRegexEngine regexEngine = _budget.RegexEngine
            ?? throw new JqException("regex engine is not configured");
        if (_patterns.TryGetValue((pattern, options), out Pattern? cached))
        {
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
            // provider may retain no pattern text; record-local input can expire.
            // The key owner follows the compiled handle through all its borrowers.
            keys.Resize(checked(128 + 2L * pattern.Length));
            _budget.CheckCancellation();
            string ownedPattern = new(pattern.AsSpan());
            try
            {
                if (!regexEngine.TryCompile(ownedPattern, options, _budget.MemoryBudget,
                    _budget.CancellationToken, out compiled, out string? error))
                    throw new JqException(error ?? "unsupported regex");
            }
            catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
            { throw new JqHostFailureException("regex compilation violated its contract", exception); }
            _budget.CheckCancellation();
            cached = new Pattern(this, ownedPattern, options, compiled, keys);
            var lease = new JqMemoryValue<Pattern>(cached, cached);
            _patterns.Add((ownedPattern, options), cached);
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
        _patterns.Remove((pattern.Text, pattern.Options));
        pattern.Compiled.Dispose();
        pattern.Keys.Dispose();
    }

    // Each operation and result holds its own borrow. Only the last
    // two returned idle patterns are reusable; active entries are never evicted.
    internal sealed class Pattern(JqRegexCache cache, string text, JqRegexCompileOptions options,
        IJqCompiledRegex compiled, JqMemoryReservation keys) : IDisposable
    {
        internal string Text { get; } = text;
        internal JqRegexCompileOptions Options { get; } = options;
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
