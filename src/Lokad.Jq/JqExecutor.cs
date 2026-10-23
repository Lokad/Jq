using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal static class JqExecutor
{
    public static async Task<int> ExecuteAsync(IJqHost host, JqCommandInvocation commandInvocation,
        IJqRegexEngine regexEngine, IMemoryBudget? memoryBudget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var memoryScope = memoryBudget?.CreateScope();
        using var budget = new JqBudget(regexEngine, memoryScope, cancellationToken);
        JqInvocation invocation;
        try
        {
            invocation = JqCommandLineParser.Parse(commandInvocation.Arguments,
                commandInvocation.CurrentDirectory, commandInvocation.StdIn, commandInvocation.StdOut,
                commandInvocation.StdErr, commandInvocation.Environment, commandInvocation.Clock, budget);
        }
        catch (JqQuotaException exception)
        {
            await JqDiagnosticWriter.WriteQuotaAsync(host, commandInvocation.StdErr,
                exception.Message, memoryScope, cancellationToken).ConfigureAwait(false);
            return 5;
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
                return await host.AppendAsync(invocation.StdOut, Utf8Text.Encode(information), cancellationToken).ConfigureAwait(false);
            }
            catch (JqQuotaException exception)
            {
                await JqDiagnosticWriter.WriteQuotaAsync(host, invocation.StdErr, exception.Message, memoryScope,
                    cancellationToken).ConfigureAwait(false);
                return 5;
            }
        }
        JqProgramSource programSource = invocation.FilterFile is { } programPath
            ? JqProgramSource.File(programPath.Display)
            : JqProgramSource.Inline;
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
        static IReadOnlyList<string> RenderUncaughtError(JqContext context, Exception exception)
        {
            var parts = new List<string> { "jq: error (at ", context.InputFilename ?? "<unknown>" };
            if (context.InputFilename is not null)
            {
                parts.Add(":");
                parts.Add(context.InputLineNumber.ToString(CultureInfo.InvariantCulture));
            }
            if (exception is JqErrorException user
                && JqRuntime.TryGetString(user.Payload, out string text))
            {
                parts.Add("): ");
                parts.Add(text);
            }
            else if (exception is JqErrorException userPayload)
            {
                parts.Add(") (not a string): ");
                parts.Add(userPayload.Payload is null
                    ? "null"
                    : context.Runtime.ToJqString(userPayload.Payload));
            }
            else
            {
                parts.Add("): ");
                parts.Add(exception.Message);
            }
            return parts;
        }

        var stage = 2;
        int stickyError = 0;
        try
        {
            var filterText = invocation.Filter ?? ".";
            if (invocation.FilterFile is { } filterFile)
            {
                using var content = await ReadFileAsync(filterFile, host, budget, cancellationToken).ConfigureAwait(false);
                ReadOnlyMemory<byte> bytes = content.Content;
                if (bytes.Span.Contains((byte)0))
                    throw new JqException("program file contains NUL bytes");
                budget.ReserveString(Encoding.UTF8.GetCharCount(bytes.Span));
                filterText = Utf8Text.Decode(bytes);
            }
            await LoadFileVariablesAsync(host, invocation, context, cancellationToken).ConfigureAwait(false);
            stage = 3;
            var tokens = Lexer.Tokenize(filterText, programSource, budget);
            var preParser = new JqParser(tokens, programSource, context.RootEnvironment, budget);
            (_, IReadOnlyList<JqModuleImport> mainImports, int bodyIndex) = preParser.ParsePrefix();
            string mainImporterDir = invocation.FilterFile is { } mainProgramPath
                ? ParentDir(mainProgramPath.Absolute.Path)
                : invocation.WorkingDirectory.Path;
            var libraryDirs = new List<string>();
            foreach (var lib in invocation.LibraryDirs)
                libraryDirs.Add(lib.Absolute.Path);
            var loader = new JqModuleLoader(host, budget, context.Runtime, context.RootEnvironment, libraryDirs, invocation.WorkingDirectory.Path);
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
                while (true)
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
                                || (output is JsonValue negative && negative.TryGetValue<bool>(out bool flag) && !flag);
                            ReadOnlyMemory<byte> rendered = outputRenderer.Render(output);
                            var appended = await JqHostExtensions.GuardedAppendAsync(host, invocation.StdOut, rendered, cancellationToken).ConfigureAwait(false);
                            if (!appended.CanAcceptMore)
                            {
                                if (appended.IsError) cursor.RecordFailure();
                                return appended.ExitCode == 0 ? stickyError : appended.ExitCode;
                            }
                        }
                    }
                    catch (Exception exception) when (JqErrors.IsCatchable(exception))
                    {
                        cursor.RecordFailure();
                        if (await WriteErrorAsync(host, invocation, RenderUncaughtError(context, exception),
                            memoryScope, cancellationToken).ConfigureAwait(false) is not { CanAcceptMore: true })
                        {
                            unwindError = exception;
                            return 5;
                        }
                        stickyError = 5;
                    }
                }
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
            return ex.ExitCode == 0 ? stickyError : ex.ExitCode;
        }
        catch (JqHaltException ex)
        {
            // Immediate termination: pending outputs and remaining inputs
            // are abandoned. String payloads render raw; anything else was
            // already shaped at the throw site.
            if (ex.StderrText is not null)
            {
                if (await JqDiagnosticWriter.TryWriteAsync(host, invocation.StdErr, [ex.StderrText],
                    false, memoryScope, cancellationToken).ConfigureAwait(false) is null)
                    return 5;
            }
            return ex.ExitCode;
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
            // Malformed inputs report status 5 and missing input operands
            // report status 2, matching the reference command.
            return await WriteErrorAsync(host, invocation, ["jq: ", ex.Message], memoryScope,
                cancellationToken).ConfigureAwait(false) is not null ? ex.ExitCode : 5;
        }
        catch (JqCloseException ex)
        {
            return await WriteErrorAsync(host, invocation, ["jq: error closing ", ex.Path, ": ", ex.Error.Message], memoryScope,
                cancellationToken).ConfigureAwait(false) is not null ? stage == 4 ? 5 : stage : 5;
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

        static async IAsyncEnumerable<JsonNode?> OuterInputsAsync(
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
                    (bool HasValue, JsonNode? Value) pulled;
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

    // Hosted `--rawfile`/`--slurpfile` variables resolve after the filter
    // file so failures surface as system errors before compilation. Argument
    // binding has already selected the first occurrence of each name.
    private static async Task LoadFileVariablesAsync(
        IJqHost host, JqInvocation invocation, JqContext context, CancellationToken cancellationToken)
    {
        if (invocation.FileVariables.Count == 0)
            return;
        var variables = invocation.Variables;
        foreach (var (name, path, raw) in invocation.FileVariables)
        {
            string which = raw ? "rawfile" : "slurpfile";
            try
            {
                long pathLength = (long)path.Length + invocation.WorkingDirectory.Path.Length + 1;
                context.Budget.ReserveBytes(128 + 64 * pathLength);
                var resolved = JqPathResolution.ResolveArgument(path, invocation.WorkingDirectory);
                using var content = await ReadFileAsync(resolved, host, context.Budget, cancellationToken).ConfigureAwait(false);
                ReadOnlyMemory<byte> bytes = content.Content;
                JsonNode? value;
                if (raw)
                {
                    context.Budget.ReserveString(Encoding.UTF8.GetCharCount(bytes.Span));
                    value = JsonValue.Create(Utf8Text.Decode(bytes));
                }
                else
                {
                    value = context.Runtime.ReadJsonArray(bytes.Span, 0);
                }
                if (name is not "ENV" and not "ARGS")
                    variables[name] = value;
                if (variables.TryGetValue("ARGS", out JsonNode? args)
                    && args is JsonObject argsObject
                    && argsObject.TryGetPropertyValue("named", out JsonNode? named)
                    && named is JsonObject namedObject)
                {
                    namedObject[name] = value;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && exception is not JqQuotaException
                && exception is not JqHostFailureException
                && exception is not JqCloseException
                && exception is not OutOfMemoryException)
            {
                throw new JqException($"Bad JSON in --{which} {name} {path}: {exception.Message}");
            }
        }
    }

    private static string ParentDir(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        int slash = canonicalPath.LastIndexOf((char)47);
        if (slash <= 0)
            return "/";
        return canonicalPath[..slash];
    }

    private static async Task<JqReadResult.Complete> ReadFileAsync(
        JqResolvedPath path, IJqHost host, JqBudget budget, CancellationToken cancellationToken)
    {
        var opened = await JqHostExtensions.GuardedOpenAsync(host, path.Absolute, cancellationToken).ConfigureAwait(false);
        if (opened.FileDescriptor is not { } descriptor)
            throw new JqException($"cannot open {path.Display}");
        bool failed = false;
        JqReadResult.Complete? content = null;
        try
        {
            content = await ReadAllAsync(host, descriptor, budget, cancellationToken).ConfigureAwait(false);
            return content;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            try { await host.CloseOwnedDescriptorAsync(descriptor, path.Display, failed).ConfigureAwait(false); }
            catch { content?.Dispose(); throw; }
        }
    }

    private static async Task<JqReadResult.Complete> ReadAllAsync(
        IJqHost host, JqFileDescriptor descriptor, JqBudget budget, CancellationToken cancellationToken)
    {
        var result = await host.TryReadAllBytesAsync(
            descriptor, budget, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            JqReadResult.Complete complete => complete,
            JqReadResult.Failed => throw new JqException("input read failed"),
            _ => throw new InvalidOperationException("Unknown bounded read result.")
        };
    }

    private static Task<JqAppendResult?> WriteErrorAsync(IJqHost host, JqInvocation invocation,
        IReadOnlyList<string> parts, IMemoryBudget? memoryBudget, CancellationToken cancellationToken) =>
        JqDiagnosticWriter.TryWriteAsync(host, invocation.StdErr, parts, true, memoryBudget, cancellationToken);
}
