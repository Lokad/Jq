using System;
using System.Collections.Generic;
using System.Text;
using Lokad.Jq.Helpers;
using Lokad.Utf8Regex.Pcre2;

namespace Lokad.Jq;

/// <summary>Caches managed PCRE2 patterns within one jq execution.</summary>
internal sealed class JqRegexCache : IDisposable
{
    private const int MaximumPatternLength = 16 * 1024;
    private const int MaximumWorkingBytes = 256 * 1024;
    private readonly JqBudget _budget;
    private readonly JqMemoryReservation _memory;
    private bool _disposed;
    private const int MaximumIdleBytes = 4096;
    private OperationMemory? _idleSubject;
    private OperationMemory? _idleMatch;
    private int _borrowers;
    private readonly Dictionary<(string Pattern, Pcre2CompileOptions Options), Pattern> _patterns = new();

    internal JqRegexCache(JqBudget budget)
    {
        _budget = budget;
        _memory = new JqMemoryReservation(budget.MemoryBudget);
    }

    internal Pattern Get(string pattern, Pcre2CompileOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        if (_patterns.TryGetValue((pattern, options), out Pattern? cached)) return cached;
        if (pattern.Length > MaximumPatternLength)
            throw new JqQuotaException("regex pattern exceeds the 16384-character limit");
        // Conservative compilation and pooled workspace reservations. These are jq
        // allocation charges, not a claim about the engine's compiled representation size.
        long estimate = 64 * 1024L + MaximumWorkingBytes + 64L * pattern.Length;
        _budget.CheckCancellation();
        long previous = _memory.LastAdmittedBytes;
        _memory.Resize(checked(previous + estimate));
        bool retained = false;
        try
        {
            var regex = new Utf8Pcre2Regex(pattern, options | Pcre2CompileOptions.Ucp,
                new Utf8Pcre2CompileSettings
                {
                    Newline = Pcre2NewlineConvention.Lf,
                    AllowDuplicateNames = true,
                    BackslashC = Pcre2BackslashCPolicy.Forbid
                },
                new Utf8Pcre2ExecutionLimits
                {
                    MatchLimit = (uint)_budget.Policy.MaximumRegexWork,
                    DepthLimit = 256,
                    HeapLimitInBytes = MaximumWorkingBytes
                }, _budget.Policy.MaximumRegexTime);
            _budget.CheckCancellation();
            _budget.CheckCancellation();
            if (regex.NameEntryCount != 0)
                _memory.Resize(checked(_memory.LastAdmittedBytes + 64L * regex.NameEntryCount));
            var entries = new Pcre2NameEntry[regex.NameEntryCount];
            regex.CopyNameEntries(entries, out _);
            var names = new Dictionary<int, string>();
            foreach (Pcre2NameEntry entry in entries) names[entry.Number] = entry.Name;
            cached = new Pattern(regex, names, pattern.Length + 1);
            _patterns.Add((pattern, options), cached);
            retained = true;
            return cached;
        }
        catch (Pcre2CompileException ex)
        {
            throw new JqException($"invalid regex: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new JqException($"unsupported regex: {ex.Message}");
        }
        finally
        {
            if (!retained) _memory.Resize(previous);
            _budget.CheckCancellation();
        }
    }

    internal JqMemoryValue<ReadOnlyMemory<byte>> EncodeSubject(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long estimate = 64L + Encoding.UTF8.GetByteCount(text);
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
        int start, Pcre2MatchOptions options)
    {
        // A numeric capture slot needs at least one pattern character. Reserve the
        // upper bound before the engine materializes detailed results; do not parse
        // its grammar here or treat its reserved MaxResultBytes as an enforced limit.
        ObjectDisposedException.ThrowIf(_disposed, this);
        long estimate = 128L + 64L * pattern.MaximumCaptureSlots;
        _budget.CheckCancellation();
        OperationMemory memory = Rent(subject: false);
        try
        {
            memory.PrepareMatch(estimate);
            return new MatchResult(MatchCore(), memory, memory.Generation);
        }
        catch
        {
            memory.Reject();
            throw;
        }

        Utf8Pcre2MatchContext MatchCore()
        {
            try { return pattern.Regex.MatchDetailed(input.Span, start, options); }
            catch (Pcre2MatchException ex) { throw MapMatchFailure(ex); }
            catch (NotSupportedException ex) { throw new JqException($"unsupported regex: {ex.Message}"); }
            finally { _budget.CheckCancellation(); }
        }
    }

    internal bool IsMatch(Pattern pattern, ReadOnlyMemory<byte> input, Pcre2MatchOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.CheckCancellation();
        try { return pattern.Regex.IsMatch(input.Span, 0, options); }
        catch (Pcre2MatchException ex) { throw MapMatchFailure(ex); }
        catch (NotSupportedException ex) { throw new JqException($"unsupported regex: {ex.Message}"); }
        finally { _budget.CheckCancellation(); }
    }

    private static JqException MapMatchFailure(Pcre2MatchException exception) => exception.ErrorKind switch
    {
        Pcre2ErrorKind.MatchLimit => new JqQuotaException("regex work limit exceeded"),
        Pcre2ErrorKind.Timeout => new JqQuotaException("regex time limit exceeded"),
        Pcre2ErrorKind.DepthLimit or Pcre2ErrorKind.HeapLimit => new JqQuotaException($"regex matching failed: {exception.Message}"),
        _ => new JqException($"regex matching failed: {exception.Message}")
    };

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
        _patterns.Clear();
        _memory.Dispose();
    }

    // One idle subject buffer and one idle detailed-result allowance, each at
    // most 4 KiB. Nested active operations rent separate owners. Failed work
    // drops its owner; disposal waits for borrowers instead of refunding early.
    internal sealed class OperationMemory(JqRegexCache cache, bool subject, IMemoryBudget? budget) : IDisposable
    {
        private readonly JqMemoryReservation _memory = new(budget);
        private byte[] _buffer = [];
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
            int length = Encoding.UTF8.GetByteCount(text);
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

        internal void PrepareMatch(long bytes)
        {
            if (bytes > _memory.LastAdmittedBytes) _memory.Resize(bytes);
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
            _memory.Dispose();
        }

        public void Dispose() => Return(Generation);
    }

    internal sealed record Pattern(Utf8Pcre2Regex Regex, Dictionary<int, string> Names, int MaximumCaptureSlots);

    internal readonly ref struct MatchResult(Utf8Pcre2MatchContext value, OperationMemory memory, long generation)
    {
        internal Utf8Pcre2MatchContext Value { get; } = value;

        public void Dispose() => memory.Return(generation);
    }
}
