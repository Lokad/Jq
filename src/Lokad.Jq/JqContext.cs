using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal sealed class JqContext(
    IReadOnlyDictionary<string, JsonNode?> variables,
    JqProgramSource programSource,
    JqBudget budget) : IDisposable
{
    public IReadOnlyDictionary<string, JsonNode?> Variables { get; } = variables;
    public JqProgramSource ProgramSource { get; } = programSource ?? throw new ArgumentNullException(nameof(programSource));
    public JqEnvironment RootEnvironment { get; } = JqEnvironment.CreateRoot(variables);
    public JqBudget Budget { get; } = budget;
    public JqRuntime Runtime { get; } = new(budget);
    public JqRegexCache Regexes { get; } = new(budget);

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
    // synchronously while the executor drains to the host between outputs,
    // so retained bytes stay bounded by one output plus its diagnostics.
    private readonly List<ReadOnlyMemory<byte>> _stderr = new();
    private readonly JqMemoryReservation _stderrMemory = new(budget.MemoryBudget);

    internal bool HasPendingStderr => _stderr.Count > 0;

    internal void EmitStderr(string text)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        Budget.ChargeAllocationWork(length);
        long previous = _stderrMemory.Bytes;
        _stderrMemory.Resize(checked(previous + length));
        try { _stderr.Add(Utf8Text.Encode(text)); }
        catch
        {
            _stderrMemory.Resize(previous);
            throw;
        }
    }

    internal void EmitStderr(ReadOnlyMemory<byte> content, bool newline)
    {
        int length = checked(content.Length + (newline ? 1 : 0));
        Budget.ChargeAllocationWork(length);
        long previous = _stderrMemory.Bytes;
        _stderrMemory.Resize(checked(previous + length));
        try
        {
            var bytes = new byte[length];
            content.Span.CopyTo(bytes);
            if (newline) bytes[^1] = (byte)'\n';
            _stderr.Add(bytes);
        }
        catch
        {
            _stderrMemory.Resize(previous);
            throw;
        }
    }

    internal void ReleaseDrainedStderr() => _stderrMemory.Resize(0);

    internal List<ReadOnlyMemory<byte>> TakePendingStderr()
    {
        var taken = new List<ReadOnlyMemory<byte>>(_stderr);
        _stderr.Clear();
        return taken;
    }

    public void Dispose()
    {
        Regexes.Dispose();
        _stderr.Clear();
        _stderrMemory.Dispose();
        Budget.Dispose();
    }
}
