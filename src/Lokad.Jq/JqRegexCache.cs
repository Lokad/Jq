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
        _budget.ChargeAllocationWork(estimate);
        long previous = _memory.Bytes;
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
            _budget.ChargeAllocationWork(64L * regex.NameEntryCount);
            _memory.Resize(checked(_memory.Bytes + 64L * regex.NameEntryCount));
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
        _budget.ChargeAllocationWork(estimate);
        var memory = new JqMemoryReservation(_budget.MemoryBudget);
        try
        {
            memory.Resize(estimate);
            return new JqMemoryValue<ReadOnlyMemory<byte>>(Utf8Text.Encode(text), memory);
        }
        catch (EncoderFallbackException ex)
        {
            memory.Dispose();
            throw new JqException($"invalid regex input: {ex.Message}");
        }
        catch
        {
            memory.Dispose();
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
        _budget.ChargeAllocationWork(estimate);
        var memory = new JqMemoryReservation(_budget.MemoryBudget);
        try
        {
            memory.Resize(estimate);
            return new MatchResult(MatchCore(), memory);
        }
        catch
        {
            memory.Dispose();
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
        _patterns.Clear();
        _memory.Dispose();
    }

    internal sealed record Pattern(Utf8Pcre2Regex Regex, Dictionary<int, string> Names, int MaximumCaptureSlots);

    internal readonly ref struct MatchResult(Utf8Pcre2MatchContext value, JqMemoryReservation memory)
    {
        internal Utf8Pcre2MatchContext Value { get; } = value;

        public void Dispose() => memory.Dispose();
    }
}
