using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal static class JqExecutor
{
    public static async Task<int> ExecuteAsync(IJqHost host, JqCommandInvocation commandInvocation,
        IJqRegexEngine? regexEngine, IMemoryBudget? memoryBudget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var memoryScope = memoryBudget?.CreateScope();
        using var budget = new JqBudget(regexEngine, memoryScope, cancellationToken);
        JqInvocation invocation;
        try
        {
            invocation = await JqCommandLineParser.ParseAsync(host, commandInvocation.Arguments,
                commandInvocation.CurrentDirectory, commandInvocation.StdIn, commandInvocation.StdOut,
                commandInvocation.StdErr, commandInvocation.Environment, commandInvocation.Clock, budget).ConfigureAwait(false);
        }
        catch (JqQuotaException exception)
        {
            await JqDiagnosticWriter.WriteQuotaAsync(host, commandInvocation.StdErr,
                exception.Message, memoryScope, cancellationToken).ConfigureAwait(false);
            return 5;
        }
        catch (JqOperationException exception)
        {
            return await WriteOperationErrorAsync(host, commandInvocation.StdErr, exception,
                2, memoryScope, cancellationToken).ConfigureAwait(false);
        }
        catch (JqCloseException exception)
        {
            return await JqDiagnosticWriter.TryWriteAsync(host, commandInvocation.StdErr,
                ["jq: error closing ", exception.Path, ": ", exception.Error.Message], true, memoryScope,
                cancellationToken).ConfigureAwait(false) is not null ? 2 : 5;
        }
        if (invocation.Error != null)
        {
            return await WriteErrorAsync(host, invocation, [invocation.Error], memoryScope,
                cancellationToken).ConfigureAwait(false) is not null ? 2 : 5;
        }
        string? information = invocation.Help ? JqHelp.Text
            : invocation.Version ? "Lokad jq\n"
            : invocation.BuildConfiguration ? JqBuildConfiguration.Text + "\n" : null;
        if (information is not null)
        {
            try
            {
                budget.CheckCancellation();
                using var informationMemory = new JqMemoryReservation(memoryScope);
                informationMemory.Resize(Encoding.UTF8.GetByteCount(information));
                budget.CheckCancellation();
                return await host.AppendAsync(invocation.StdOut, Utf8Text.Encode(information), cancellationToken).ConfigureAwait(false);
            }
            catch (JqQuotaException exception)
            {
                await JqDiagnosticWriter.WriteQuotaAsync(host, invocation.StdErr, exception.Message, memoryScope,
                    cancellationToken).ConfigureAwait(false);
                return 5;
            }
        }
        JqProgramSource programSource = JqProgramSource.Inline;
        // Binding creates an execution-private variable table. File variables
        // and ARGS.named updates stay isolated across reused/concurrent commands.
        using var context = new JqContext(invocation.Variables, budget)
        {
            Clock = invocation.Clock,
            Stderr = (host, invocation.StdErr),
            SortKeys = invocation.SortKeys,
            AsciiOutput = invocation.AsciiOutput,
        };
        using var outputRenderer = new JqRuntime.OutputRenderer(context.Runtime, budget, invocation);
        // Uncaught evaluation failures render with the failing input's position
        // like the reference process loop: string payloads print raw while other
        // payloads and error shapes print rendered with a not-a-string marker.
        // Inputs never read report <unknown>; line numbers count the newlines
        // consumed through the failing value, so values on unterminated tails
        // report one past the reference fgets-based count.
        static async Task<JqAppendResult?> WriteUncaughtErrorAsync(IJqHost host, JqInvocation invocation,
            JqContext context, Exception exception, IMemoryBudget? memoryBudget, CancellationToken cancellationToken)
        {
            var parts = new List<string> { "jq: error (at ", context.InputFilename ?? "<unknown>" };
            if (context.InputFilename is not null)
            {
                parts.Add(":");
                parts.Add(context.InputLineNumber.ToString(CultureInfo.InvariantCulture));
            }
            if (exception is JqErrorException { Payload: JqString text })
            {
                parts.Add("): ");
                return await JqDiagnosticWriter.TryWriteAsync(host, invocation.StdErr, parts,
                    text.Utf8, true, memoryBudget, cancellationToken).ConfigureAwait(false);
            }
            else if (exception is JqErrorException userPayload)
            {
                parts.Add(") (not a string): ");
                using var rendered = context.Runtime.SerializeUtf8(userPayload.Payload, false, null, false);
                return await JqDiagnosticWriter.TryWriteAsync(host, invocation.StdErr, parts,
                    rendered.Value, true, memoryBudget, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                parts.Add("): ");
                parts.Add(exception.Message);
            }
            return await WriteErrorAsync(host, invocation, parts, memoryBudget, cancellationToken).ConfigureAwait(false);
        }

        var stage = 2;
        int stickyError = 0;
        try
        {
            if (invocation.FilterFile is { } programPath)
                programSource = JqProgramSource.File(programPath.Path, budget);
            var filterText = invocation.Filter ?? ".";
            if (invocation.FilterFile is { } filterFile)
            {
                using var content = await host.ReadFileAsync(filterFile, budget, cancellationToken).ConfigureAwait(false);
                ReadOnlyMemory<byte> bytes = content.Content;
                if (bytes.Span.Contains((byte)0))
                    throw new JqException("program file contains NUL bytes");
                budget.ReserveString(Encoding.UTF8.GetCharCount(bytes.Span));
                filterText = Utf8Text.Decode(bytes);
            }
            stage = 3;
            var tokens = Lexer.Tokenize(filterText, programSource, budget);
            var preParser = new JqParser(tokens, programSource, context.RootEnvironment, budget);
            (_, IReadOnlyList<JqModuleImport> mainImports, int bodyIndex) = preParser.ParsePrefix();
            string mainImporterDir = mainImports.Count != 0 && invocation.FilterFile is { } mainProgramPath
                ? JqPathResolution.ParentDirectory(JqPathResolution.Location(mainProgramPath, budget), budget)
                : invocation.WorkingDirectory.Path;
            var libraryDirs = new List<string>();
            foreach (var lib in invocation.LibraryDirs)
                libraryDirs.Add(JqPathResolution.Location(lib, budget));
            var loader = new JqModuleLoader(host, budget, context.Runtime, context.RootEnvironment, libraryDirs, invocation.WorkingDirectory);
            context.ModuleLoader = loader;
            JqEnvironment moduleEnv = await loader.LoadMainImportsAsync(mainImports, mainImporterDir, cancellationToken).ConfigureAwait(false);
            // Resume after the validated prefix with dependencies in scope.
            budget.CheckCancellation();
            var filter = new JqParser(tokens, programSource, moduleEnv, budget).ParseBody(bodyIndex);
            stage = 4;
            await using var cursor = new JqInputCursor(host, invocation, context, cancellationToken);
            context.InputCursor = cursor;
            Exception? unwindError = null;
            try
            {
                await using var inputs = OuterInputsAsync(cursor, invocation, context).GetAsyncEnumerator();
                bool sawOutput = false;
                bool lastFalseNull = false;
                while (!cursor.HasOpenFailures)
                {
                    stage = 4;
                    cursor.ReleaseCompletedRecordStorage();
                    budget.BeginRecord();
                    if (!await inputs.MoveNextAsync().ConfigureAwait(false)) break;
                    stage = 5;
                    try
                    {
                        await foreach (var output in filter.Evaluate(inputs.Current, context, moduleEnv).ConfigureAwait(false))
                        {
                            sawOutput = true;
                            lastFalseNull = output is null
                                || (output is JqScalar negative && negative.TryGetValue<bool>(out bool flag) && !flag);
                            ReadOnlyMemory<byte> rendered = outputRenderer.Render(output);
                            var appended = await JqHostExtensions.GuardedAppendAsync(host, invocation.StdOut, rendered, cancellationToken).ConfigureAwait(false);
                            if (!appended.CanAcceptMore)
                            {
                                if (appended.Error is { } error)
                                    throw new JqOperationException("writing", "<stdout>", error);
                                return cursor.HasOpenFailures ? 2 : stickyError;
                            }
                        }
                    }
                    catch (Exception exception) when (JqErrors.IsCatchable(exception))
                    {
                        cursor.RecordFailure();
                        if (await WriteUncaughtErrorAsync(host, invocation, context, exception,
                            memoryScope, cancellationToken).ConfigureAwait(false) is not { CanAcceptMore: true })
                        {
                            unwindError = exception;
                            return 5;
                        }
                        stickyError = 5;
                    }
                }
                if (cursor.HasOpenFailures) return 2;
                if (stickyError != 0)
                    return stickyError;
                if (invocation.ExitStatus)
                    return !sawOutput ? 4 : lastFalseNull ? 1 : 0;
                return 0;
            }
            catch (Exception unwind)
            {
                unwindError = unwind is JqOutputClosedException { ExitCode: 0 }
                    or JqHaltException { ExitCode: 0 } ? null : unwind;
                throw;
            }
            finally
            {
                await cursor.CloseAbandonedAsync(unwindError).ConfigureAwait(false);
            }
        }
        catch (JqOutputClosedException ex)
        {
            return ex.ExitCode == 0 ? context.InputCursor?.HasOpenFailures == true ? 2 : stickyError : ex.ExitCode;
        }
        catch (JqHaltException ex)
        {
            // The record owner outlives this catch and any pending byte borrow.
            try
            {
                if (ex.Payload is JqString text)
                    await JqHostExtensions.GuardedAppendAsync(host, invocation.StdErr, text.Utf8,
                        cancellationToken).ConfigureAwait(false);
                else if (ex.Payload is not null)
                {
                    using var rendered = context.Runtime.SerializeLine(ex.Payload);
                    await JqHostExtensions.GuardedAppendAsync(host, invocation.StdErr, rendered.Value,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (JqQuotaException refusal)
            {
                await JqDiagnosticWriter.WriteQuotaAsync(host, invocation.StdErr, refusal.Message,
                    memoryScope, cancellationToken).ConfigureAwait(false);
                return 5;
            }
            return context.InputCursor?.HasOpenFailures == true ? 2 : ex.ExitCode;
        }
        catch (JqQuotaException ex)
        {
            // Resource policy failures always report status 5, no matter which
            // stage was active when the budget tripped.
            // A stopped diagnostic descriptor prevents further writes, but
            // cannot replace the resource failure with success or another code.
            await JqDiagnosticWriter.WriteQuotaAsync(host, invocation.StdErr, ex.Message, memoryScope,
                cancellationToken).ConfigureAwait(false);
            return 5;
        }
        catch (JqInputException ex)
        {
            // Preserve the input failure's status; an earlier failed open
            // retains system status 2 outside jq handlers.
            return await WriteErrorAsync(host, invocation, ["jq: ", ex.Message], memoryScope,
                cancellationToken).ConfigureAwait(false) is not null
                ? context.InputCursor?.HasOpenFailures == true ? 2 : ex.ExitCode : 5;
        }
        catch (JqCloseException ex)
        {
            return await WriteErrorAsync(host, invocation, ["jq: error closing ", ex.Path, ": ", ex.Error.Message], memoryScope,
                cancellationToken).ConfigureAwait(false) is not null ? stage == 4 ? 5 : stage : 5;
        }
        catch (JqOperationException ex)
        {
            return await WriteOperationErrorAsync(host, invocation.StdErr, ex, stage == 4 ? 5 : stage, memoryScope,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JqCompileException ex)
        {
            return await WriteErrorAsync(host, invocation,
                ["jq: ", ex.Message, " at line ", ex.Span.Line.ToString(CultureInfo.InvariantCulture),
                    " column ", ex.Span.Column.ToString(CultureInfo.InvariantCulture),
                    " (", ex.ProgramSource.Label, ")"], memoryScope,
                cancellationToken).ConfigureAwait(false) is not null ? stage : 5;
        }
        catch (Exception ex) when (ex is JqException or JsonException or FormatException or ArgumentException or OverflowException)
        {
            return await WriteErrorAsync(host, invocation, ["jq: ", ex.Message], memoryScope,
                cancellationToken).ConfigureAwait(false) is not null ? stage : 5;
        }

        static async IAsyncEnumerable<JqValue?> OuterInputsAsync(
            JqInputCursor cursor, JqInvocation invocation, JqContext context)
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
                    (bool HasValue, JqValue? Value) pulled;
                    try
                    {
                        pulled = await cursor.PullAsync().ConfigureAwait(false);
                    }
                    catch (JqSeqResyncException resync)
                    {
                        await context.EmitStderrAsync("jq: ignoring parse error: " + resync.Message + "\n").ConfigureAwait(false);
                        continue;
                    }
                    catch (JqException input) when (input is not JqQuotaException and not JqInputException and not JqCloseException)
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
                using var rawSlurped = new JqJsonBuffer(context.Budget);
                while (true)
                {
                    (JqString? line, bool terminated) =
                        await cursor.PullRawSegmentAsync().ConfigureAwait(false);
                    if (line is null)
                        break;
                    int length = JqBudget.BufferCapacity((long)line.ByteLength + (terminated ? 1 : 0), 0);
                    JqBudget.BufferCapacity((long)rawSlurped.WrittenMemory.Length + length, 0);
                    if (length != 0)
                    {
                        Span<byte> target = rawSlurped.GetSpan(length);
                        line.Utf8.Span.CopyTo(target);
                        if (terminated) target[line.ByteLength] = (byte)'\n';
                        rawSlurped.Advance(length);
                    }
                }
                context.InputFilename = cursor.LastName;
                context.InputLineNumber = cursor.LastLine;
                yield return JqString.FromUtf8(rawSlurped.WrittenMemory.Span, context.Budget);
                yield break;
            }
            var slurped = new JqArray();
            while (true)
            {
                (bool HasValue, JqValue? Value) pulled;
                try
                {
                    pulled = await cursor.PullAsync().ConfigureAwait(false);
                }
                catch (JqSeqResyncException resync)
                {
                    await context.EmitStderrAsync("jq: ignoring parse error: " + resync.Message + "\n").ConfigureAwait(false);
                    continue;
                }
                catch (JqException input) when (input is not JqQuotaException and not JqInputException and not JqCloseException)
                {
                    throw new JqInputException("parse error: " + input.Message, 5);
                }
                if (!pulled.HasValue)
                    break;
                context.Budget.ReserveBytes(24);
                slurped.Add(pulled.Value);
            }
            context.InputFilename = cursor.LastName;
            context.InputLineNumber = cursor.LastLine;
            yield return slurped;
        }
    }

    private static async Task<int> WriteOperationErrorAsync(IJqHost host, JqFileDescriptor stderr,
        JqOperationException exception, int status, IMemoryBudget? memoryBudget, CancellationToken cancellationToken)
    {
        return await JqDiagnosticWriter.TryWriteAsync(host, stderr,
            [exception.Operation == "opening" ? "jq: cannot open " : "jq: error " + exception.Operation + " ",
                exception.Path, ": ", exception.Error.Message],
            true, memoryBudget, cancellationToken).ConfigureAwait(false) is not null
            ? exception.Operation == "writing" ? 1 : status : 5;
    }

    private static Task<JqAppendResult?> WriteErrorAsync(IJqHost host, JqInvocation invocation,
        IReadOnlyList<string> parts, IMemoryBudget? memoryBudget, CancellationToken cancellationToken) =>
        JqDiagnosticWriter.TryWriteAsync(host, invocation.StdErr, parts, true, memoryBudget, cancellationToken);
}
