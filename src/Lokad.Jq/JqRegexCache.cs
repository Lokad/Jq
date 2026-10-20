using System;
using System.Collections.Generic;
using System.Text;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

/// <summary>Caches adapted regex patterns within one jq execution.</summary>
internal sealed class JqRegexCache : IDisposable
{
    private readonly JqBudget _budget;
    private readonly JqMemoryReservation _keys;
    private bool _disposed;
    private const int MaximumIdleBytes = 4096;
    private SubjectMemory? _idleSubject;
    private int _borrowers;
    private readonly Dictionary<(string Pattern, JqRegexCompileOptions Options), IJqCompiledRegex> _patterns = new();

    internal JqRegexCache(JqBudget budget)
    {
        _budget = budget;
        _keys = new JqMemoryReservation(budget.MemoryBudget);
    }

    internal IJqCompiledRegex Get(string pattern, JqRegexCompileOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        if (_patterns.TryGetValue((pattern, options), out IJqCompiledRegex? cached)) return cached;
        IJqCompiledRegex? compiled = null;
        long previousKeys = _keys.LastAdmittedBytes;
        bool retained = false;
        try
        {
            // Own the persistent key before giving it to a borrowed engine. The
            // provider may retain no pattern text; record-local input can expire.
            // One coarse cache owner covers keys, entries and backing metadata.
            _keys.Resize(checked(previousKeys + 128 + 2L * pattern.Length));
            _budget.CheckCancellation();
            string ownedPattern = new(pattern.AsSpan());
            if (!_budget.RegexEngine.TryCompile(ownedPattern, options, _budget.MemoryBudget,
                _budget.CancellationToken, out compiled, out string? error))
                throw new JqException(error ?? "unsupported regex");
            _budget.CheckCancellation();
            cached = compiled;
            _patterns.Add((ownedPattern, options), cached);
            retained = true;
            return cached;
        }
        catch (JqRegexResourceException exception) { throw Terminal(exception); }
        finally
        {
            if (!retained)
            {
                compiled?.Dispose();
                if (_keys.LastAdmittedBytes != previousKeys) _keys.Resize(previousKeys);
            }
        }
    }

    internal JqMemoryValue<ReadOnlyMemory<byte>> EncodeSubject(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        SubjectMemory memory = RentSubject();
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

    internal JqRegexMatch Match(IJqCompiledRegex pattern, ReadOnlyMemory<byte> input,
        int start, JqRegexMatchOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        JqRegexMatch match = default;
        bool retained = false;
        try
        {
            match = pattern.Match(input.Span, start, options, _budget.CancellationToken);
            _budget.CheckCancellation();
            var lifetime = new MatchLifetime(this, match.Owner);
            _borrowers++;
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

    internal bool IsMatch(IJqCompiledRegex pattern, ReadOnlyMemory<byte> input, JqRegexMatchOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        try { return pattern.IsMatch(input.Span, options, _budget.CancellationToken); }
        catch (JqRegexResourceException exception) { throw Terminal(exception); }
        catch (NotSupportedException ex) { throw new JqException($"unsupported regex: {ex.Message}"); }
        finally { _budget.CheckCancellation(); }
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
        if (_borrowers == 0) ReleaseCompiled();
    }

    private SubjectMemory RentSubject()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SubjectMemory memory = _idleSubject ?? new SubjectMemory(this, _budget.MemoryBudget);
        _idleSubject = null;
        memory.Begin();
        _borrowers++;
        return memory;
    }

    private void Return(SubjectMemory memory, bool keep)
    {
        if (!keep || _disposed || memory.Capacity > MaximumIdleBytes)
            memory.Destroy();
        else if (_idleSubject is null)
            _idleSubject = memory;
        else
            memory.Destroy();
        ReleaseBorrower();
    }

    private void ReleaseBorrower()
    {
        _borrowers--;
        if (_disposed && _borrowers == 0) ReleaseCompiled();
    }

    private void ReleaseCompiled()
    {
        foreach (IJqCompiledRegex pattern in _patterns.Values) pattern.Dispose();
        _patterns.Clear();
        _keys.Dispose();
    }

    // Keep one encoded subject within 4 KiB. Nested subjects have separate
    // owners; disposal waits for borrowers instead of refunding early.
    private sealed class SubjectMemory(JqRegexCache cache, IMemoryBudget? budget) : IDisposable
    {
        private readonly JqMemoryReservation _memory = new(budget);
        private byte[] _buffer = [];
        private bool _leased;
        internal long Capacity => _memory.LastAdmittedBytes;

        internal void Begin()
        {
            _leased = true;
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
        }

        internal void Reject()
        {
            if (!_leased) return;
            _leased = false;
            cache.Return(this, false);
        }

        internal void Destroy()
        {
            _buffer = [];
            _memory.Dispose();
        }
    }

    private sealed class MatchLifetime(JqRegexCache cache, IDisposable? owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { owner?.Dispose(); }
            finally { cache.ReleaseBorrower(); }
        }
    }
}
