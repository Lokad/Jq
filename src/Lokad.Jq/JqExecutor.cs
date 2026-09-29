using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal static class JqExecutor
{
    public static async Task<int> ExecuteAsync(IJqHost host, JqInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Version || invocation.BuildConfiguration)
        {
            await host.AppendAsync(invocation.StdOut, Utf8Text.Encode("Lokad jq\n"), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        if (invocation.Error != null)
        {
            await WriteErrorAsync(host, invocation, invocation.Error, cancellationToken).ConfigureAwait(false);
            return 2;
        }

        var budget = new JqBudget(cancellationToken);
        JqProgramSource programSource = invocation.FilterFile is { } programPath
            ? JqProgramSource.File(Utf8Text.Decode(programPath.Display))
            : JqProgramSource.Inline;
        using var context = new JqContext(invocation.Variables, programSource, budget) { Clock = invocation.Clock };
        var stage = 2;
        try
        {
            var filterText = invocation.Filter ?? ".";
            if (invocation.FilterFile is { } filterFile)
            {
                var bytes = await ReadFileAsync(filterFile, host, budget, cancellationToken).ConfigureAwait(false);
                budget.ChargeString(Encoding.UTF8.GetCharCount(bytes.Span));
                filterText = Utf8Text.Decode(bytes);
            }
            stage = 3;
            var filter = new JqParser(filterText, programSource, context.RootEnvironment, budget).Parse();
            stage = 4;
            foreach (var value in invocation.Variables.Values) budget.ChargeTree(value);
            await using var inputs = ReadInputsAsync(host, invocation, context, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                stage = 4;
                if (!await inputs.MoveNextAsync().ConfigureAwait(false)) break;
                stage = 5;
                foreach (var output in filter.Evaluate(inputs.Current, context, context.RootEnvironment))
                {
                    ReadOnlyMemory<byte> rendered;
                    if (invocation.RawOutput && JqRuntime.TryGetString(output, out var text))
                    {
                        budget.ChargeOutput(Encoding.UTF8.GetByteCount(text) + (invocation.JoinOutput ? 0 : 1));
                        rendered = invocation.JoinOutput ? Utf8Text.Encode(text) : Utf8Text.EncodeLine(text);
                    }
                    else
                    {
                        var body = context.Runtime.SerializeUtf8(output, invocation.AsciiOutput, invocation.Indent, invocation.UseTabs);
                        budget.ChargeOutput(body.Length + (invocation.JoinOutput ? 0 : 1));
                        rendered = invocation.JoinOutput ? body : ByteLines.AppendNewline(body);
                    }
                    var appended = await JqHostExtensions.GuardHostAsync(() => host.AppendWhileOpenAsync(invocation.StdOut, rendered, cancellationToken)).ConfigureAwait(false);
                    if (!appended.CanAcceptMore) return appended.ExitCode;
                    if (await DrainStderrAsync(host, invocation, context, cancellationToken).ConfigureAwait(false) is int stopped)
                        return stopped;
                }
            }
            if (await DrainStderrAsync(host, invocation, context, cancellationToken).ConfigureAwait(false) is int tailStopped)
                return tailStopped;
            return 0;
        }
        catch (JqHaltException ex)
        {
            // Immediate termination: pending outputs and remaining inputs
            // are abandoned. String payloads render raw; anything else was
            // already shaped at the throw site.
            if (await DrainStderrAsync(host, invocation, context, cancellationToken).ConfigureAwait(false) is int haltStopped)
                return haltStopped;
            if (ex.StderrText is not null)
                await host.AppendAsync(invocation.StdErr, Utf8Text.Encode(ex.StderrText), cancellationToken).ConfigureAwait(false);
            return ex.ExitCode;
        }
        catch (JqCompileException ex)
        {
            await WriteErrorAsync(host, invocation, "jq: " + ex.Message + " at line " + ex.Span.Line + " column " + ex.Span.Column + " (" + ex.ProgramSource.Label + ")", cancellationToken).ConfigureAwait(false);
            return stage;
        }
        catch (Exception ex) when (ex is JqException or JsonException or FormatException or ArgumentException or OverflowException)
        {
            if (await DrainStderrAsync(host, invocation, context, cancellationToken).ConfigureAwait(false) is int errorStopped)
                return errorStopped;
            await WriteErrorAsync(host, invocation, $"jq: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return stage;
        }

        static async Task<int?> DrainStderrAsync(IJqHost host, JqInvocation invocation, JqContext context, CancellationToken cancellationToken)
        {
            if (!context.HasPendingStderr)
                return null;
            foreach (ReadOnlyMemory<byte> chunk in context.TakePendingStderr())
            {
                JqAppendResult appended = await JqHostExtensions.GuardHostAsync(() => host.AppendWhileOpenAsync(invocation.StdErr, chunk, cancellationToken)).ConfigureAwait(false);
                if (!appended.CanAcceptMore)
                    return appended.ExitCode;
            }
            return null;
        }
        static async IAsyncEnumerable<JsonNode?> ReadInputsAsync(
            IJqHost host, JqInvocation invocation, JqContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (invocation.NullInput)
            {
                yield return null;
                yield break;
            }

            var slurped = new JsonArray();
            var rawSlurped = new StringBuilder();
            var count = Math.Max(1, invocation.InputFiles.Count);
            for (var i = 0; i < count; i++)
            {
                var bytes = invocation.InputFiles.Count == 0
                    ? await ReadAllAsync(host, invocation.StdIn, context.Budget, cancellationToken).ConfigureAwait(false)
                    : await ReadFileAsync(invocation.InputFiles[i], host, context.Budget, cancellationToken).ConfigureAwait(false);
                if (invocation.RawInput)
                {
                    if (invocation.Slurp)
                    {
                        context.Budget.ChargeString(Encoding.UTF8.GetCharCount(bytes.Span));
                        context.Budget.Append(rawSlurped, Utf8Text.Decode(bytes));
                    }
                    else
                    {
                        // Raw input is LF-only; CR is ordinary data.
                        var start = 0;
                        while (start < bytes.Length)
                        {
                            var length = bytes.Span[start..].IndexOf((byte)'\n');
                            if (length < 0) length = bytes.Length - start;
                            var line = bytes.Slice(start, length);
                            context.Budget.ChargeNode();
                            context.Budget.ChargeString(Encoding.UTF8.GetCharCount(line.Span));
                            yield return JsonValue.Create(Utf8Text.Decode(line));
                            start += length + 1;
                        }
                    }
                }
                else
                {
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        if (bytes.Span[offset..].TrimStart(" \t\r\n"u8).IsEmpty) break;
                        var node = context.Runtime.ReadJsonValue(bytes.Span[offset..], out var consumed);
                        offset += consumed;
                        if (invocation.Slurp) slurped.Add(node);
                        else yield return node;
                    }
                }
            }
            if (invocation.Slurp)
                yield return invocation.RawInput ? JsonValue.Create(context.Budget.Finish(rawSlurped)) : slurped;
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadFileAsync(
        JqResolvedPath path, IJqHost host, JqBudget budget, CancellationToken cancellationToken)
    {
        var opened = await JqHostExtensions.GuardHostAsync(() => host.OpenReadAsync(path.Absolute, cancellationToken)).ConfigureAwait(false);
        if (opened.Error != null || opened.FileDescriptor == null)
            throw new JqException($"cannot open {Utf8Text.Decode(path.Display)}");
        Exception? failure = null;
        try
        {
            return await ReadAllAsync(host, opened.FileDescriptor.Value, budget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await host.CloseOwnedDescriptorAsync(opened.FileDescriptor.Value, failure).ConfigureAwait(false);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadAllAsync(
        IJqHost host, JqFileDescriptor descriptor, JqBudget budget, CancellationToken cancellationToken)
    {
        var result = await host.TryReadAllBytesAsync(
            descriptor, JqBudget.MaximumInputBytes, budget.ChargeInput, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            BoundedReadResult.Complete complete => complete.Content,
            BoundedReadResult.Failed => throw new JqException("input read failed"),
            BoundedReadResult.TooLarge => throw new JqException("input exceeds the 16 MiB limit"),
            _ => throw new InvalidOperationException("Unknown bounded read result.")
        };
    }

    private static async Task WriteErrorAsync(IJqHost host, JqInvocation invocation, string message, CancellationToken cancellationToken)
    {
        await host.AppendAsync(invocation.StdErr, Utf8Text.Encode(message + "\n"), cancellationToken).ConfigureAwait(false);
    }
}
