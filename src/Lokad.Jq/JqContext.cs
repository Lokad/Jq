using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
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

    internal (IJqHost Host, JqFileDescriptor Descriptor)? Stderr { get; init; }

    private bool _disposed;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask EmitStderrAsync(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int length = JqBudget.BufferCapacity(Utf8Text.GetByteCount(text, Budget.CancellationToken), 0);
        if (length == 0)
        {
            await EmitStderrAsync(ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            return;
        }
        JqJsonBuffer buffer = Budget.RentSerializationBuffer();
        IDisposable owner;
        try
        {
            buffer.Advance(Utf8Text.Encode(text, buffer.GetSpan(length)));
            owner = Budget.OwnSerializationBuffer(buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        using (owner)
            await EmitStderrAsync(buffer.WrittenMemory).ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask EmitStderrAsync(ReadOnlyMemory<byte> content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Budget.CheckCancellation();
        var (host, descriptor) = Stderr
            ?? throw new InvalidOperationException("Diagnostic output requires a host descriptor.");
        JqAppendResult appended = await JqHostExtensions.GuardedAppendAsync(
            host, descriptor, content, Budget.CancellationToken).ConfigureAwait(false);
        Budget.CheckCancellation();
        if (!appended.CanAcceptMore)
            throw new JqHaltException(appended.ExitCode, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Regexes.Dispose();
        Budget.Dispose();
    }
}
