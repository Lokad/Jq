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
    private OperationMemory? _idleSubject;
    private OperationMemory? _idleMatch;
    private int _borrowers;
    private readonly Dictionary<(string Pattern, JqRegexCompileOptions Options), Pattern> _patterns = new();

    internal JqRegexCache(JqBudget budget)
    {
        _budget = budget;
    }

    internal Pattern Get(string pattern, JqRegexCompileOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        if (_patterns.TryGetValue((pattern, options), out Pattern? cached)) return cached;
        IJqCompiledRegex? compiled = null;
        bool retained = false;
        try
        {
            if (!_budget.Policy.RegexEngine.TryCompile(pattern, options, _budget.MemoryBudget,
                _budget.CancellationToken, out compiled, out string? error))
                throw new JqException(error ?? "unsupported regex");
            _budget.CheckCancellation();
            if (compiled.CaptureSlotCapacity < 1)
                throw new InvalidOperationException("The regex adapter returned an invalid capture capacity.");
            cached = new Pattern(compiled, compiled.GroupNames, compiled.CaptureSlotCapacity);
            _patterns.Add((pattern, options), cached);
            retained = true;
            return cached;
        }
        catch (JqRegexResourceException exception) { throw Terminal(exception); }
        finally
        {
            if (!retained) compiled?.Dispose();
        }
    }

    internal JqMemoryValue<ReadOnlyMemory<byte>> EncodeSubject(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        OperationMemory memory = Rent(subject: true);
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

    internal MatchResult Match(Pattern pattern, ReadOnlyMemory<byte> input,
        int start, JqRegexMatchOptions options)
    {
        // The adapter supplies a capture-slot bound. Admit destination scratch and
        // conservative producer-result storage before detailed materialization.
        ObjectDisposedException.ThrowIf(_disposed, this);
        long estimate = 128L + 64L * pattern.MaximumCaptureSlots;
        _budget.CheckCancellation();
        OperationMemory memory = Rent(subject: false);
        try
        {
            memory.PrepareMatch(estimate, pattern.MaximumCaptureSlots);
            int count = pattern.Regex.Match(input.Span, start, options, memory.Captures, _budget.CancellationToken);
            _budget.CheckCancellation();
            if ((uint)count > (uint)pattern.MaximumCaptureSlots)
                throw new InvalidOperationException("The regex adapter returned an invalid capture count.");
            return new MatchResult(new JqRegexMatch(memory.Captures.AsSpan(0, count)), memory, memory.Generation);
        }
        catch (JqRegexResourceException exception)
        {
            memory.Reject();
            throw Terminal(exception);
        }
        catch (NotSupportedException exception)
        {
            memory.Reject();
            throw new JqException("unsupported regex: " + exception.Message);
        }
        catch
        {
            memory.Reject();
            throw;
        }
    }

    internal bool IsMatch(Pattern pattern, ReadOnlyMemory<byte> input, JqRegexMatchOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        try { return pattern.Regex.IsMatch(input.Span, options, _budget.CancellationToken); }
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
        _idleMatch?.Destroy();
        _idleMatch = null;
        if (_borrowers == 0) ReleaseCompiled();
    }

    private OperationMemory Rent(bool subject)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        OperationMemory memory = (subject ? _idleSubject : _idleMatch)
            ?? new OperationMemory(this, subject, _budget.MemoryBudget);
        if (subject) _idleSubject = null;
        else _idleMatch = null;
        memory.Begin();
        _borrowers++;
        return memory;
    }

    private void Return(OperationMemory memory, bool keep)
    {
        _borrowers--;
        if (!keep || _disposed || memory.Capacity > MaximumIdleBytes)
            memory.Destroy();
        else if (memory.IsSubject && _idleSubject is null)
            _idleSubject = memory;
        else if (!memory.IsSubject && _idleMatch is null)
            _idleMatch = memory;
        else
            memory.Destroy();
        if (_disposed && _borrowers == 0) ReleaseCompiled();
    }

    private void ReleaseCompiled()
    {
        foreach (Pattern pattern in _patterns.Values) pattern.Regex.Dispose();
        _patterns.Clear();
    }

    // One idle subject buffer and one idle detailed-result allowance, each at
    // most 4 KiB. Nested active operations rent separate owners. Failed work
    // drops its owner; disposal waits for borrowers instead of refunding early.
    internal sealed class OperationMemory(JqRegexCache cache, bool subject, IMemoryBudget? budget) : IDisposable
    {
        private readonly JqMemoryReservation _memory = new(budget);
        private byte[] _buffer = [];
        internal JqRegexCapture[] Captures { get; private set; } = [];
        private bool _leased;
        internal bool IsSubject => subject;
        internal long Capacity => _memory.LastAdmittedBytes;
        internal long Generation { get; private set; }

        internal void Begin()
        {
            _leased = true;
            Generation++;
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

        internal void PrepareMatch(long bytes, int slots)
        {
            if (slots > Captures.Length)
            {
                // A returned result has ended its borrow. Scratch needs no copy,
                // so end its old storage lifetime before allocating a replacement.
                // Keep admitted credit; refusal still fails without retry/eviction.
                Captures = [];
                if (bytes > _memory.LastAdmittedBytes) _memory.Resize(bytes);
                Captures = new JqRegexCapture[slots];
            }
            else if (bytes > _memory.LastAdmittedBytes) _memory.Resize(bytes);
        }

        internal void Return(long generation)
        {
            if (!_leased || generation != Generation) return;
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
            Captures = [];
            _memory.Dispose();
        }

        public void Dispose() => Return(Generation);
    }

    internal sealed record Pattern(IJqCompiledRegex Regex, IReadOnlyDictionary<int, string> Names, int MaximumCaptureSlots);

    internal readonly ref struct MatchResult(JqRegexMatch value, OperationMemory memory, long generation)
    {
        internal JqRegexMatch Value { get; } = value;

        public void Dispose() => memory.Return(generation);
    }
}

internal readonly ref struct JqRegexMatch(ReadOnlySpan<JqRegexCapture> captures)
{
    private ReadOnlySpan<JqRegexCapture> Captures { get; } = captures;
    internal bool Success => Captures.Length != 0 && Captures[0].Success;
    internal JqRegexCapture Value => Captures[0];
    internal int CaptureSlotCount => Captures.Length;
    internal bool TryGetGroup(int index, out JqRegexCapture capture)
    {
        if ((uint)index < (uint)Captures.Length) { capture = Captures[index]; return true; }
        capture = default;
        return false;
    }
}
