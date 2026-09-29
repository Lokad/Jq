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
            var preParser = new JqParser(filterText, programSource, context.RootEnvironment, budget);
            (_, IReadOnlyList<JqModuleImport> mainImports) = preParser.ParseImportsOnly();
            string mainImporterDir = invocation.FilterFile is { } mainProgramPath
                ? ParentDir(mainProgramPath.Absolute.Path)
                : invocation.WorkingDirectory.Path;
            var libraryDirs = new List<string>();
            foreach (var lib in invocation.LibraryDirs)
                libraryDirs.Add(lib.Absolute.Path);
            var loader = new JqModuleLoader(host, budget, context.Runtime, context.RootEnvironment, libraryDirs, invocation.WorkingDirectory.Path);
            context.ModuleLoader = loader;
            JqEnvironment moduleEnv = await loader.LoadMainImportsAsync(mainImports, mainImporterDir, cancellationToken).ConfigureAwait(false);
            var filter = new JqParser(filterText, programSource, moduleEnv, budget).Parse();
            stage = 4;
            foreach (var value in invocation.Variables.Values) budget.ChargeTree(value);
            await using var cursor = new JqInputCursor(host, invocation, context, cancellationToken);
            context.InputCursor = cursor;
            await using var inputs = OuterInputsAsync(cursor, invocation, context, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                stage = 4;
                if (!await inputs.MoveNextAsync().ConfigureAwait(false)) break;
                stage = 5;
                foreach (var output in filter.Evaluate(inputs.Current, context, moduleEnv))
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
                        int framing = (invocation.Seq ? 1 : 0) + (invocation.JoinOutput ? 0 : 1);
                        budget.ChargeOutput(body.Length + framing);
                        ReadOnlyMemory<byte> framed = invocation.Seq ? PrefixRecordSeparator(body) : body;
                        rendered = invocation.JoinOutput ? framed : ByteLines.AppendNewline(framed);
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
        static async IAsyncEnumerable<JsonNode?> OuterInputsAsync(
            JqInputCursor cursor, JqInvocation invocation, JqContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(cursor);
            if (invocation.NullInput)
            {
                context.InputFilename = null;
                context.InputLineNumber = 0;
                yield return null;
                yield break;
            }
            if (!invocation.Slurp)
            {
                while (true)
                {
                    (bool HasValue, JsonNode? Value) pulled;
                    try
                    {
                        pulled = await cursor.PullAsync().ConfigureAwait(false);
                    }
                    catch (JqSeqResyncException resync)
                    {
                        WarnIgnoringParseError(context, resync.Message);
                        continue;
                    }
                    catch (JqException input) when (input is not JqQuotaException)
                    {
                        throw new JqException("parse error: " + input.Message);
                    }
                    if (!pulled.HasValue)
                        break;
                    yield return pulled.Value;
                }
                yield break;
            }
            if (invocation.RawInput)
            {
                var rawSlurped = new StringBuilder();
                while (true)
                {
                    (bool hasValue, string line, bool terminated) =
                        await cursor.PullRawSegmentAsync().ConfigureAwait(false);
                    if (!hasValue)
                        break;
                    context.Budget.Append(rawSlurped, line.AsSpan());
                    if (terminated)
                        context.Budget.Append(rawSlurped, "\n".AsSpan());
                }
                context.InputFilename = cursor.LastName;
                context.InputLineNumber = cursor.LastLine;
                yield return JsonValue.Create(context.Budget.Finish(rawSlurped));
                yield break;
            }
            var slurped = new JsonArray();
            while (true)
            {
                (bool HasValue, JsonNode? Value) pulled;
                try
                {
                    pulled = await cursor.PullAsync().ConfigureAwait(false);
                }
                catch (JqSeqResyncException resync)
                {
                    WarnIgnoringParseError(context, resync.Message);
                    continue;
                }
                catch (JqException input) when (input is not JqQuotaException)
                {
                    throw new JqException("parse error: " + input.Message);
                }
                if (!pulled.HasValue)
                    break;
                slurped.Add(pulled.Value);
            }
            context.InputFilename = cursor.LastName;
            context.InputLineNumber = cursor.LastLine;
            yield return slurped;
        }
    }

    private static ReadOnlyMemory<byte> PrefixRecordSeparator(ReadOnlyMemory<byte> body)
    {
        var framed = new byte[body.Length + 1];
        framed[0] = 30;
        body.Span.CopyTo(framed.AsSpan(1));
        return framed;
    }

    // Sequence-mode record failures are warnings, never fatal: the cursor
    // already resynchronized past the separator, so outer iteration continues.
    private static void WarnIgnoringParseError(JqContext context, string message)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(message);
        context.EmitStderr(Utf8Text.Encode("jq: ignoring parse error: " + message + "\n").ToArray());
    }

    private static string ParentDir(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        int slash = canonicalPath.LastIndexOf((char)47);
        if (slash <= 0)
            return "/";
        return canonicalPath[..slash];
    }

    private static int CountNewlines(ReadOnlyMemory<byte> bytes, int start, int end)
    {
        int count = 0;
        ReadOnlySpan<byte> span = bytes.Span;
        for (int index = start; index < end; index++)
        {
            if (span[index] == (byte)10)
                count++;
        }
        return count;
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
