using System;
using System.Collections.Generic;
using System.Text;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

/// <summary>Caches adapted regex patterns within one jq execution.</summary>
internal sealed class JqRegexCache : IDisposable
{
    private readonly JqBudget _budget;
    private bool _disposed;
    private const int MaximumIdleBytes = 4096;
    private SubjectMemory? _idleSubject;
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
                if (!_budget.RegexEngine.TryCompile(ownedPattern, options, _budget.MemoryBudget,
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

    internal JqMemoryValue<ReadOnlyMemory<byte>> EncodeSubject(string text) => EncodeSubject(text, null);

    internal JqMemoryValue<ReadOnlyMemory<byte>> EncodeSubject(string text, Pattern? pattern)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        SubjectMemory memory = _idleSubject ?? new SubjectMemory(this, _budget.MemoryBudget);
        _idleSubject = null;
        memory.Begin(pattern);
        try
        {
            return new JqMemoryValue<ReadOnlyMemory<byte>>(memory.Encode(text), memory);
        }
        catch (EncoderFallbackException ex)
        {
            memory.Reject();
            throw new JqException($"invalid regex input: {ex.Message}");
        }
        catch
        {
            memory.Reject();
            throw;
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
        _idleSubject?.Destroy();
        _idleSubject = null;
        RetireOlderIdlePattern();
        if (_idlePattern is { } idle)
        {
            _idlePattern = null;
            Retire(idle);
        }
    }

    private void Return(SubjectMemory memory, bool keep)
    {
        if (!keep || _disposed || memory.Capacity > MaximumIdleBytes)
            memory.Destroy();
        else if (_idleSubject is null)
            _idleSubject = memory;
        else
            memory.Destroy();
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

    // Each operation, subject and result holds its own borrow. Only the last
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

    // Keep one encoded subject within 4 KiB. Nested subjects have separate
    // owners; disposal waits for borrowers instead of refunding early.
    private sealed class SubjectMemory(JqRegexCache cache, IMemoryBudget? budget) : IDisposable
    {
        private readonly JqMemoryReservation _memory = new(budget);
        private byte[] _buffer = [];
        private bool _leased;
        private Pattern? _pattern;
        internal long Capacity => _memory.LastAdmittedBytes;

        internal void Begin(Pattern? pattern)
        {
            _leased = true;
            _pattern = pattern;
            if (pattern is not null) pattern.Borrowers++;
        }

        internal ReadOnlyMemory<byte> Encode(string text)
        {
            int length = JqBudget.BufferCapacity(Utf8Text.GetByteCount(text, cache._budget.CancellationToken), 0);
            if (_memory.LastAdmittedBytes == 0 || length > _buffer.Length)
            {
                long previous = _memory.LastAdmittedBytes;
                int oldCapacity = _buffer.Length;
                // Keep optional growth within the existing idle-owner ceiling.
                // Large subjects and the first allocation retain exact capacity.
                int capacity = oldCapacity != 0 && length <= MaximumIdleBytes - 64
                    ? Math.Max(length, Math.Min(MaximumIdleBytes - 64, oldCapacity * 2)) : length;
                _memory.Resize(64L + oldCapacity + capacity);
                try { _buffer = new byte[capacity]; }
                catch { _memory.Resize(previous); throw; }
                if (oldCapacity != 0) _memory.Resize(64L + capacity);
            }
            int written = Utf8Text.Encode(text, _buffer);
            return _buffer.AsMemory(0, written);
        }

        public void Dispose()
        {
            if (!_leased) return;
            _leased = false;
            cache.Return(this, true);
            ReleasePattern();
        }

        internal void Reject()
        {
            if (!_leased) return;
            _leased = false;
            cache.Return(this, false);
            ReleasePattern();
        }

        private void ReleasePattern()
        {
            Pattern? pattern = _pattern;
            _pattern = null;
            pattern?.Dispose();
        }

        internal void Destroy()
        {
            _buffer = [];
            _memory.Dispose();
        }
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
