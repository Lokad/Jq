using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal sealed class JqContext(
    IReadOnlyDictionary<string, JsonNode?> variables,
    JqBudget budget) : IDisposable
{
    public IReadOnlyDictionary<string, JsonNode?> Variables { get; } = variables;
    public JqEnvironment RootEnvironment { get; } = JqEnvironment.CreateRoot(variables);
    public JqBudget Budget { get; } = budget;
    public JqRuntime Runtime { get; } = new(budget);
    public JqRegexCache Regexes { get; } = new(budget);

    private uint _nextLabel;
    internal uint NextLabel() => unchecked(_nextLabel++);

    // Explicit host clock for time builtins; null means the capability is absent.
    public JqClock? Clock { get; init; }

    // Link-time module loader for `modulemeta`. Set by the executor after
    // resolving imports; null only for contexts that never evaluate filters.
    internal JqModuleLoader? ModuleLoader { get; set; }

    // Whether `--sort-keys` orders object keys on JSON output (debug
    // rendering follows; diagnostics, `tostring`, and raw strings do not).
    public bool SortKeys { get; init; }

    // Whether `--ascii-output` escapes non-ASCII output (debug rendering
    // follows like sorted keys; `stderr` values and `tostring` do not).
    public bool AsciiOutput { get; init; }

    // Shared pull cursor over every input source. Set by the executor before
    // evaluation so implicit iteration and explicit `input`/`inputs` calls
    // draw from the same stream.
    internal JqInputCursor? InputCursor { get; set; }

    // Input cursor metadata for input_filename and input_line_number. The
    // executor refreshes both before evaluating each input value.
    public string? InputFilename { get; set; }

    public int InputLineNumber { get; set; }

    // Diagnostic byte queue for debug and stderr filters. Filters enqueue
    // synchronously while the executor drains between outputs or at completion.
    // A filter can queue many diagnostics before producing its next output.
    private const int StderrCreditBytes = 256;
    private readonly List<ReadOnlyMemory<byte>> _stderr = new();
    private readonly JqMemoryReservation _stderrMemory = new(budget.MemoryBudget);
    private long _stderrBytes;
    private int _smallStderrMessages;
    private bool _disposed;

    internal bool HasPendingStderr => _stderr.Count > 0;

    internal void EmitStderr(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int length = JqBudget.BufferCapacity(Utf8Text.GetByteCount(text, Budget.CancellationToken), 0);
        Budget.CheckCancellation();
        long previous = ReserveStderr(length);
        try
        {
            Budget.CheckCancellation();
            _stderr.Add(Utf8Text.Encode(text));
        }
        catch
        {
            if (_stderrMemory.LastAdmittedBytes != previous) _stderrMemory.Resize(previous);
            throw;
        }
        CommitStderr(length);
    }

    internal void EmitStderr(ReadOnlyMemory<byte> content, bool newline)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int length = JqBudget.BufferCapacity((long)content.Length + (newline ? 1 : 0), 0);
        Budget.CheckCancellation();
        long previous = ReserveStderr(length);
        try
        {
            Budget.CheckCancellation();
            var bytes = length == 0 ? Array.Empty<byte>() : new byte[length];
            content.Span.CopyTo(bytes);
            if (newline) bytes[^1] = (byte)'\n';
            _stderr.Add(bytes);
        }
        catch
        {
            if (_stderrMemory.LastAdmittedBytes != previous) _stderrMemory.Resize(previous);
            throw;
        }
        CommitStderr(length);
    }

    private long ReserveStderr(int length)
    {
        if (_stderr.Count == _stderr.Capacity)
        {
            int capacity = JqBudget.BufferCapacity((long)_stderr.Count + 1, Math.Max(4, 2L * _stderr.Capacity));
            long previousBacking = QueueBytes(_stderr.Capacity);
            long previousAdmission = _stderrMemory.LastAdmittedBytes;
            long backing = QueueBytes(capacity);
            _stderrMemory.Resize(checked(previousAdmission + backing));
            try
            {
                Budget.CheckCancellation();
                _stderr.Capacity = capacity;
            }
            catch
            {
                _stderrMemory.Resize(previousAdmission);
                throw;
            }
            if (previousBacking != 0) _stderrMemory.Resize(previousAdmission + backing - previousBacking);
        }
        long previous = _stderrMemory.LastAdmittedBytes;
        long required = checked(QueueBytes(_stderr.Capacity) + _stderrBytes + MessageBytes(length));
        if (required > previous)
        {
            long target = required;
            int quantum = required > 4096 ? 4096 : StderrCreditBytes;
            if (length < StderrCreditBytes && _smallStderrMessages >= 31
                && required <= long.MaxValue - (quantum - 1))
                target = ((required + quantum - 1) / quantum) * quantum;
            _stderrMemory.Resize(target);
        }
        return previous;
    }

    private void CommitStderr(int length)
    {
        _stderrBytes += MessageBytes(length);
        if (length is > 0 and < StderrCreditBytes)
            _smallStderrMessages = Math.Min(32, _smallStderrMessages + 1);
    }

    internal void ReleaseDrainedStderr()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stderr.Clear();
        // Keep the small queue used by ordinary diagnostics; release burst
        // inventories before refunding their backing admission.
        if (_stderr.Capacity > 4) _stderr.Capacity = 0;
        long previous = _stderrMemory.LastAdmittedBytes;
        long backing = QueueBytes(_stderr.Capacity);
        long retained = backing + (_smallStderrMessages == 32 ? Math.Min(previous - backing, StderrCreditBytes) : 0);
        if (previous != retained) _stderrMemory.Resize(retained);
        _stderrBytes = 0;
    }

    private static long QueueBytes(int capacity) => capacity == 0 ? 0 : 32 + 16L * capacity;

    private static long MessageBytes(int length) => length == 0 ? 0 : 32L + length;

    // Evaluation and drains are serialized. The queue and its byte owners stay
    // live through every pending host write; clear only when the drain ends.
    internal IReadOnlyList<ReadOnlyMemory<byte>> PendingStderr
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _stderr;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Regexes.Dispose();
        _stderr.Clear();
        _stderrMemory.Dispose();
        _stderrBytes = 0;
        Budget.Dispose();
    }
}
