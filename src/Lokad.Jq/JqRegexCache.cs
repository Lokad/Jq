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

    // Transfers an explicit byte-owner lease, including on failure. A null
    // owner borrows the runtime's funded record through all operation borrowers.
    // Native immutable bytes enter directly, without encoding or copying.
    internal SubjectLease Prepare(Pattern pattern, ReadOnlyMemory<byte> bytes, IDisposable? owner)
    {
        IJqRegexSubject? subject = null;
        bool retained = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _budget.CheckCancellation();
                subject = pattern.Compiled.Prepare(bytes, _budget.MemoryBudget, _budget.CancellationToken);
            }
            catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
            { throw new JqHostFailureException("regex preparation violated its contract", exception); }
            _budget.CheckCancellation();
            var lease = new SubjectLease(subject, bytes, owner, pattern);
            pattern.Borrowers++;
            retained = true;
            return lease;
        }
        catch (JqRegexResourceException exception) { throw Terminal(exception); }
        catch (NotSupportedException exception) { throw new JqException("unsupported regex: " + exception.Message); }
        finally
        {
            if (!retained)
            {
                try { subject?.Dispose(); }
                finally { owner?.Dispose(); }
            }
        }
    }

    internal static int GetCaptureCount(Pattern pattern)
    {
        try { return pattern.Compiled.CaptureCount; }
        catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
        { throw new JqHostFailureException("regex metadata violated its contract", exception); }
    }

    internal static IReadOnlyDictionary<int, ReadOnlyMemory<byte>> GetGroupNames(Pattern pattern)
    {
        try { return pattern.GroupNames(); }
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
        internal ReadOnlyMemory<byte> Utf8 { get; } = utf8;
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
        private IReadOnlyDictionary<int, ReadOnlyMemory<byte>>? _names;
        internal PatternKey Key { get; } = key;
        internal IJqCompiledRegex Compiled { get; } = compiled;
        internal JqMemoryReservation Keys { get; } = keys;
        internal int Borrowers { get; set; }
        internal IReadOnlyDictionary<int, ReadOnlyMemory<byte>> GroupNames()
        {
            if (_names is { } existing) return existing;
            var entries = Compiled.GroupNames;
            Keys.Resize(192L + Key.Utf8.Length + 64L * entries.Count);
            cache._budget.CheckCancellation();
            var names = new Dictionary<int, ReadOnlyMemory<byte>>(entries.Count);
            foreach (var entry in entries)
            {
                cache._budget.CheckCancellation();
                names.Add(entry.GroupNumber, entry.NameUtf8);
            }
            _names = names;
            return names;
        }

        public void Dispose() => cache.Return(this);
    }

    // One operation retains funded native bytes and its program borrow. Active
    // cursors and retained results extend that ownership through suspension.
    internal sealed class SubjectLease(IJqRegexSubject subject,
        ReadOnlyMemory<byte> bytes, IDisposable? owner, Pattern pattern) : IJqRegexSubject
    {
        private int _borrowers = 1;
        private bool _disposed;

        internal ReadOnlyMemory<byte> Bytes => bytes;

        internal void Hold() => _borrowers++;

        internal void Release()
        {
            if (--_borrowers != 0) return;
            try { subject.Dispose(); }
            finally
            {
                try { owner?.Dispose(); }
                finally { pattern.Dispose(); }
            }
        }

        public bool IsMatch(JqRegexMatchOptions options, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool result = subject.IsMatch(options, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (JqRegexResourceException exception) { throw Terminal(exception); }
            catch (NotSupportedException exception) { throw new JqException("unsupported regex: " + exception.Message); }
            catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
            { throw new JqHostFailureException("regex matching violated its contract", exception); }
        }

        public IJqRegexCursor CreateCursor(JqRegexMatchOptions options, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            IJqRegexCursor? cursor = null;
            bool retained = false;
            try
            {
                cursor = subject.CreateCursor(options, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var lease = new CursorLease(this, cursor);
                Hold();
                retained = true;
                return lease;
            }
            catch (JqRegexResourceException exception) { throw Terminal(exception); }
            catch (NotSupportedException exception) { throw new JqException("unsupported regex: " + exception.Message); }
            catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
            { throw new JqHostFailureException("regex cursor creation violated its contract", exception); }
            finally { if (!retained) cursor?.Dispose(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release();
        }
    }

    private sealed class CursorLease(SubjectLease subject, IJqRegexCursor cursor) : IJqRegexCursor
    {
        private bool _disposed;

        public bool MoveNext(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool result = cursor.MoveNext(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (JqRegexResourceException exception) { throw Terminal(exception); }
            catch (NotSupportedException exception) { throw new JqException("unsupported regex: " + exception.Message); }
            catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
            { throw new JqHostFailureException("regex cursor advancement violated its contract", exception); }
        }

        public JqRegexMatch Current
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try { return cursor.Current; }
                catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
                { throw new JqHostFailureException("regex capture access violated its contract", exception); }
            }
        }

        public JqRegexMatch RetainCurrent(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            JqRegexMatch match = default;
            bool retained = false;
            try
            {
                match = cursor.RetainCurrent(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var lifetime = new MatchLifetime(subject, match.Owner);
                subject.Hold();
                retained = true;
                return new JqRegexMatch(match.Captures, lifetime);
            }
            catch (JqRegexResourceException exception) { throw Terminal(exception); }
            catch (NotSupportedException exception) { throw new JqException("unsupported regex: " + exception.Message); }
            catch (Exception exception) when (JqErrors.IsCatchableClr(exception))
            { throw new JqHostFailureException("regex result retention violated its contract", exception); }
            finally { if (!retained) match.Dispose(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { cursor.Dispose(); }
            finally { subject.Release(); }
        }
    }

    private sealed class MatchLifetime(SubjectLease subject, IDisposable? owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { owner?.Dispose(); }
            finally { subject.Release(); }
        }
    }
}
