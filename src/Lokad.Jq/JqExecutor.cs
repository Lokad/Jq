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
        if (invocation.Error != null)
        {
            await WriteErrorAsync(host, invocation, invocation.Error, cancellationToken).ConfigureAwait(false);
            return 2;
        }
        if (invocation.Help)
        {
            await host.AppendAsync(invocation.StdOut, Utf8Text.Encode(JqHelp.Text), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        if (invocation.Version)
        {
            await host.AppendAsync(invocation.StdOut, Utf8Text.Encode("Lokad jq\n"), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        if (invocation.BuildConfiguration)
        {
            await host.AppendAsync(invocation.StdOut, Utf8Text.Encode(JqBuildConfiguration.Text + "\n"), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var budget = new JqBudget(cancellationToken);
        JqProgramSource programSource = invocation.FilterFile is { } programPath
            ? JqProgramSource.File(Utf8Text.Decode(programPath.Display))
            : JqProgramSource.Inline;
        // Variable tables are snapshotted per execution. File variables and
        // ARGS.named entries are recorded while running, so a reused command
        // must not leak them into later executions.
        var executionVariables = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var entry in invocation.Variables)
        {
            budget.ChargeTree(entry.Value);
            executionVariables[entry.Key] = entry.Value?.DeepClone();
        }
        using var context = new JqContext(executionVariables, programSource, budget)
        {
            Clock = invocation.Clock,
            SortKeys = invocation.SortKeys,
            AsciiOutput = invocation.AsciiOutput,
        };
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
            await LoadFileVariablesAsync(host, invocation, context, cancellationToken).ConfigureAwait(false);
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
            await using var cursor = new JqInputCursor(host, invocation, context, cancellationToken);
            context.InputCursor = cursor;
            Exception? unwindError = null;
            try
            {
                await using var inputs = OuterInputsAsync(cursor, invocation, context, cancellationToken).GetAsyncEnumerator(cancellationToken);
                bool sawOutput = false;
                bool lastFalseNull = false;
                int stickyError = 0;
                while (true)
                {
                    stage = 4;
                    if (!await inputs.MoveNextAsync().ConfigureAwait(false)) break;
                    stage = 5;
                    try
                    {
                        foreach (var output in filter.Evaluate(inputs.Current, context, moduleEnv))
                        {
                            sawOutput = true;
                            lastFalseNull = output is null
                                || (output is JsonValue negative && negative.TryGetValue<bool>(out bool flag) && !flag);
                            ReadOnlyMemory<byte> rendered = RenderOutput(invocation, context, budget, output);
                            var appended = await JqHostExtensions.GuardHostAsync(() => host.AppendWhileOpenAsync(invocation.StdOut, rendered, cancellationToken)).ConfigureAwait(false);
                            if (!appended.CanAcceptMore) return appended.ExitCode;
                            if (await DrainStderrAsync(host, invocation, context, cancellationToken).ConfigureAwait(false) is int stopped)
                                return stopped;
                        }
                    }
                    catch (Exception exception) when (JqErrors.IsCatchable(exception))
                    {
                        if (await DrainStderrAsync(host, invocation, context, cancellationToken).ConfigureAwait(false) is int errorStopped)
                            return errorStopped;
                        await WriteErrorAsync(host, invocation, $"jq: {exception.Message}", cancellationToken).ConfigureAwait(false);
                        stickyError = 5;
                    }
                }
                if (await DrainStderrAsync(host, invocation, context, cancellationToken).ConfigureAwait(false) is int tailStopped)
                    return tailStopped;
                if (stickyError != 0)
                    return stickyError;
                if (invocation.ExitStatus)
                    return !sawOutput ? 4 : lastFalseNull ? 1 : 0;
                return 0;
            }
            catch (Exception unwind)
            {
                unwindError = unwind;
                throw;
            }
            finally
            {
                await cursor.CloseAbandonedAsync(unwindError).ConfigureAwait(false);
            }
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
        catch (JqQuotaException ex)
        {
            // Resource policy failures always report status 5, no matter which
            // stage was active when the budget tripped.
            if (await DrainStderrAsync(host, invocation, context, cancellationToken).ConfigureAwait(false) is int quotaStopped)
                return quotaStopped;
            await WriteErrorAsync(host, invocation, $"jq: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return 5;
        }
        catch (JqInputException ex)
        {
            // Malformed inputs report status 5 and missing input operands
            // report status 2, matching the reference command.
            if (await DrainStderrAsync(host, invocation, context, cancellationToken).ConfigureAwait(false) is int inputStopped)
                return inputStopped;
            await WriteErrorAsync(host, invocation, $"jq: {ex.Message}", cancellationToken).ConfigureAwait(false);
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
                    catch (JqException input) when (input is not JqQuotaException and not JqInputException)
                    {
                        throw new JqInputException("parse error: " + input.Message, 5);
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
                catch (JqException input) when (input is not JqQuotaException and not JqInputException)
                {
                    throw new JqInputException("parse error: " + input.Message, 5);
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

    private static ReadOnlyMemory<byte> RenderOutput(
        JqInvocation invocation,
        JqContext context,
        JqBudget budget,
        JsonNode? output)
    {
        if (invocation.RawOutput && JqRuntime.TryGetString(output, out var text))
        {
            if (invocation.RawOutput0 && text.Contains((char)0))
                throw new JqException("Cannot dump a string containing NUL with --raw-output0 option");
            if (invocation.RawOutput0)
            {
                budget.ChargeOutput(Encoding.UTF8.GetByteCount(text) + 1);
                return ByteLines.AppendTerminator(Utf8Text.Encode(text), 0);
            }
            budget.ChargeOutput(Encoding.UTF8.GetByteCount(text) + (invocation.JoinOutput ? 0 : 1));
            return invocation.JoinOutput ? Utf8Text.Encode(text) : Utf8Text.EncodeLine(text);
        }
        var body = context.Runtime.SerializeUtf8(
            output, invocation.AsciiOutput, invocation.Indent, invocation.UseTabs, invocation.SortKeys);
        int framing = (invocation.Seq ? 1 : 0) + (invocation.RawOutput0 ? 1 : invocation.JoinOutput ? 0 : 1);
        budget.ChargeOutput(body.Length + framing);
        ReadOnlyMemory<byte> framed = invocation.Seq ? PrefixRecordSeparator(body) : body;
        if (invocation.RawOutput0)
            return ByteLines.AppendTerminator(framed, 0);
        return invocation.JoinOutput ? framed : ByteLines.AppendNewline(framed);
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

    // Hosted `--rawfile`/`--slurpfile` variables resolve after the filter
    // file so failures surface as system errors before compilation. Names
    // already bound by earlier arguments keep their values without reads.
    private static async Task LoadFileVariablesAsync(
        IJqHost host, JqInvocation invocation, JqContext context, CancellationToken cancellationToken)
    {
        if (invocation.FileVariables.Count == 0)
            return;
        if (context.Variables is not Dictionary<string, JsonNode?> variables)
            throw new InvalidOperationException("File variables need a mutable variable table.");
        foreach (var (name, path, raw) in invocation.FileVariables)
        {
            if (variables.ContainsKey(name))
                continue;
            string which = raw ? "rawfile" : "slurpfile";
            try
            {
                var resolved = JqPathResolution.ResolveArgument(path, invocation.WorkingDirectory);
                var bytes = await ReadFileAsync(resolved, host, context.Budget, cancellationToken).ConfigureAwait(false);
                JsonNode? value;
                if (raw)
                {
                    context.Budget.ChargeString(Encoding.UTF8.GetCharCount(bytes.Span));
                    value = JsonValue.Create(Utf8Text.Decode(bytes));
                }
                else
                {
                    value = ReadSlurpfileValue(bytes, context);
                }
                variables[name] = value;
                context.Budget.ChargeTree(value);
                if (variables.TryGetValue("ARGS", out JsonNode? args)
                    && args is JsonObject argsObject
                    && argsObject.TryGetPropertyValue("named", out JsonNode? named)
                    && named is JsonObject namedObject)
                {
                    namedObject[name] = context.Runtime.Clone(value);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && exception is not JqQuotaException
                && exception is not JqHostFailureException)
            {
                throw new JqException($"Bad JSON in --{which} {name} {path}: {exception.Message}");
            }
        }
    }

    private static JsonArray ReadSlurpfileValue(ReadOnlyMemory<byte> bytes, JqContext context)
    {
        var values = new JsonArray();
        int offset = JqRuntime.HasBomPrefix(bytes.Span) ? 3 : 0;
        while (offset < bytes.Length)
        {
            if (bytes.Span[offset..].TrimStart(" \t\r\n"u8).IsEmpty)
                break;
            values.Add(context.Runtime.ReadJsonValue(bytes.Span[offset..], out int consumed));
            offset += consumed;
        }
        return values;
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
