using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using PCRE;

namespace Lokad.Jq;

/// <summary>Compiles each pattern once per jq execution and bounds native regex work and storage.</summary>
internal sealed class JqRegexCache : IDisposable
{
    // Small expressions suffice for text cleanup. Bound native compilation separately from JSON strings.
    private const uint MaximumPatternLength = 16 * 1024;
    private const uint MaximumCompiledBytes = 64 * 1024;
    private const uint MaximumMatchHeapKiB = 256;
    private const int MaximumWork = 10_000_000;
    // Callout positions do not expose every internal scan. Bound cumulative matching time as well,
    // excluding replacement evaluation and time spent waiting for input/output.
    private static readonly TimeSpan MaximumMatchTime = TimeSpan.FromSeconds(5);
    private static readonly PcreMatchSettings MatchSettings = new()
    {
        MatchLimit = 100_000, // Same per-match work limit as sed and grep.
        DepthLimit = 256,
        HeapLimit = MaximumMatchHeapKiB
    };

    private readonly JqBudget _budget;
    private readonly TimeProvider _clock;
    private readonly Dictionary<(string Pattern, PcreOptions Options), Pattern> _patterns = new();
    private readonly PcreRefCalloutFunc _callout;
    // Shared across patterns and input values, including unsuccessful searches.
    private int _remainingWork = MaximumWork;
    private long _remainingTicks;
    private long _matchDeadline;
    private int _lastOffset;

    internal JqRegexCache(JqBudget budget, TimeProvider clock)
    {
        _budget = budget;
        _clock = clock;
        _remainingTicks = (long)(MaximumMatchTime.TotalSeconds * clock.TimestampFrequency);
        _callout = callout =>
        {
            budget.CheckCancellation();
            // Possessive repeats can scan a long suffix between just two callouts. Charge forward
            // movement and backtracking as well as the callback itself; do not count callbacks alone.
            _remainingWork -= 1 + Math.Abs(callout.CurrentOffset - _lastOffset);
            _lastOffset = callout.CurrentOffset;
            if (_remainingWork < 0)
                throw new JqQuotaException("regex work limit exceeded");
            CheckTime();
            return PcreCalloutResult.Pass;
        };
    }

    internal Pattern Get(string pattern, PcreOptions options)
    {
        _budget.CheckCancellation();
        if (_patterns.TryGetValue((pattern, options), out var cached))
            return cached;
        if (pattern.Length > MaximumPatternLength)
            throw new JqQuotaException("regex pattern exceeds the 16384-character limit");

        _budget.ChargeBytes(MaximumCompiledBytes);
        PcreRegex regex;
        try
        {
            regex = new PcreRegex(pattern, new PcreRegexSettings
            {
                Options = options | PcreOptions.Utf | PcreOptions.Ucp | PcreOptions.AutoCallout | PcreOptions.NeverBackslashC | PcreOptions.DupNames,
                NewLine = PcreNewLine.Lf,
                ParensLimit = JqBudget.MaximumDepth,
                MaxPatternLength = MaximumPatternLength,
                MaxPatternCompiledLength = MaximumCompiledBytes
            });
        }
        catch (PcreException ex) when (ex.ErrorCode is PcreErrorCode.ParenthesesNestTooDeep
                                     or PcreErrorCode.PatternTooLarge
                                     or PcreErrorCode.PatternTooComplicated
                                     or PcreErrorCode.PatternStringTooLong)
        {
            throw new JqQuotaException($"invalid regex: {ex.Message}");
        }
        catch (PcreException ex)
        {
            throw new JqException($"invalid regex: {ex.Message}");
        }
        // Charge native capacity once per cached pattern, not once per input record.
        _budget.ChargeBytes(MaximumMatchHeapKiB * 1024L + 32L * (regex.PatternInfo.CaptureCount + 1));
        cached = new Pattern(regex);
        _patterns.Add((pattern, options), cached);
        return cached;
    }

    /// <summary>The returned match borrows the cached buffer; copy its data before evaluating more filters.</summary>
    internal PcreRefMatch Match(Pattern pattern, string input, int start, PcreMatchOptions options)
    {
        _budget.CheckCancellation();
        var started = _clock.GetTimestamp();
        _matchDeadline = started + _remainingTicks;
        _lastOffset = start;
        try
        {
            var match = pattern.Buffer.Match(input.AsSpan(), start, options, _callout);
            CheckTime(); // Optimized unsuccessful searches may not invoke any callouts.
            return match;
        }
        catch (PcreCalloutException ex) when (ex.InnerException is Exception inner
                                            && inner is JqException or OperationCanceledException)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
        catch (PcreException ex) when (ex.ErrorCode is PcreErrorCode.MatchLimit
                                     or PcreErrorCode.DepthLimit
                                     or PcreErrorCode.HeapLimit
                                     or PcreErrorCode.JitStackLimit)
        {
            throw new JqQuotaException($"regex matching failed: {ex.Message}");
        }
        catch (PcreException ex)
        {
            throw new JqException($"regex matching failed: {ex.Message}");
        }
        finally
        {
            _remainingTicks -= _clock.GetTimestamp() - started;
        }
    }

    private void CheckTime()
    {
        if (_clock.GetTimestamp() >= _matchDeadline)
            throw new JqQuotaException("regex time limit exceeded");
    }

    public void Dispose()
    {
        foreach (var pattern in _patterns.Values) pattern.Buffer.Dispose();
        _patterns.Clear();
    }

    internal sealed class Pattern(PcreRegex regex)
    {
        internal PcreRegex Regex { get; } = regex;
        internal PcreMatchBuffer Buffer { get; } = regex.CreateMatchBuffer(MatchSettings);
    }
}
