using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal static class JqCommandLineParser
{
    private static bool TryExpandShortOption(char option, out string name)
    {
        name = option switch
        {
            'V' => "version", 'b' => "binary", 'n' => "null-input",
            'R' => "raw-input", 's' => "slurp", 'c' => "compact-output",
            'r' => "raw-output", 'j' => "join-output", 'a' => "ascii-output",
            'S' => "sort-keys", 'C' => "color-output", 'M' => "monochrome-output",
            'e' => "exit-status", 'h' => "help", 'f' => "from-file",
            'L' => "library-path", _ => string.Empty
        };
        return name.Length != 0;
    }

    // Check -1..7 without overflow, including arbitrarily many leading zeros.
    private static bool IsIndentInRange(string text)
    {
        int start = text[0] is '+' or '-' ? 1 : 0;
        int first = start;
        while (first < text.Length && text[first] == '0')
            first++;
        int significant = text.Length - first;
        if (significant == 0)
            return true;
        if (significant > 1)
            return false;
        return text[0] == '-'
            ? text[first] <= '1'
            : text[first] <= '7';
    }

    // Strict `--indent` values: an optional sign followed by ASCII digits.
    private static bool IsStrictIndentValue(string text)
    {
        if (text.Length == 0)
            return false;
        int start = text[0] is '+' or '-' ? 1 : 0;
        if (start == text.Length)
            return false;
        for (int i = start; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
                return false;
        }
        return true;
    }

    // Mirrors the reference classifier: only `-X` (single dash plus an
    // ASCII letter) and `--anything` are options. Negative numbers, lone
    // `-`, and other dash-led operands are filters or files.
    private static bool Isoptish(string argument) =>
        argument.Length >= 2
        && argument[0] == '-'
        && (argument[1] == '-' || char.IsAsciiLetter(argument[1]));

    public static async ValueTask<JqInvocation> ParseAsync(
        IJqHost host, IReadOnlyList<string> args,
        JqPath currentDirectory,
        JqFileDescriptor stdIn,
        JqFileDescriptor stdOut,
        JqFileDescriptor stdErr,
        IReadOnlyList<JqEnvironmentVariable> environment,
        JqClock? clock,
        JqBudget budget)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var runtime = new JqRuntime(budget);
        try
        {
            budget.CheckCancellation();
            budget.ReserveMetadata(512, 8);
            var parsed = new Options();
            var format = OutputFormat.Default;
            var variables = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var positionalArguments = new JsonArray();
            var operands = new List<string>();
            var libraryPaths = new List<string>();
            var boundNames = new HashSet<string>(StringComparer.Ordinal);
            string? program = null;
            bool fromFile = false;
            bool optionsDone = false;
            bool positional = false;
            bool jsonArguments = false;

            for (int i = 0; i < args.Count; i++)
            {
                budget.CheckCancellation();
                string argument = args[i];
                if (optionsDone || !Isoptish(argument))
                {
                    if (program is null) program = argument;
                    else if (!positional)
                    {
                        budget.ReserveMetadata(32, 1);
                        operands.Add(argument);
                    }
                    else
                    {
                        budget.ReserveMetadata(jsonArguments ? 24 : 88, 1);
                        if (!jsonArguments) positionalArguments.Add(JsonValue.Create(argument));
                        else
                        {
                            try { positionalArguments.Add(runtime.ParseJson(argument)); }
                            catch (JqException exception) when (exception is not JqQuotaException)
                            { return Error($"jq: invalid JSON argument: {exception.Message}"); }
                        }
                    }
                    continue;
                }
                if (argument == "--")
                {
                    optionsDone = true;
                    continue;
                }
                if (argument[1] == '-')
                {
                    string option = argument[2..];
                    if (option is "rawfile" or "slurpfile")
                    {
                        if (i + 2 >= args.Count)
                            return Error("jq: --" + option + " takes two parameters (e.g. --" + option + " varname filename)");
                        string name = args[++i];
                        string path = args[++i];
                        if (!TryBindName(name)) continue;
                        try
                        {
                            ChargePathResolution(path);
                            var resolved = JqPathResolution.ResolveArgument(path, currentDirectory);
                            using var content = await host.ReadFileAsync(resolved, budget, budget.CancellationToken).ConfigureAwait(false);
                            if (option == "rawfile")
                            {
                                budget.ReserveString(System.Text.Encoding.UTF8.GetCharCount(content.Content.Span));
                                variables[name] = JsonValue.Create(Utf8Text.Decode(content.Content));
                            }
                            else variables[name] = runtime.ReadJsonArray(content.Content.Span, 0);
                        }
                        catch (Exception exception) when (JqErrors.IsCatchable(exception) || exception is JqPathException)
                        { return Error("jq: Bad JSON in --" + option + " " + name + " " + path + ": " + exception.Message); }
                    }
                    else
                    {
                        string? error = ApplyOption(option, null, ref i);
                        if (error is not null) return Error(error);
                    }
                }
                else
                {
                    for (int position = 1; position < argument.Length; position++)
                    {
                        budget.CheckCancellation();
                        char flag = argument[position];
                        if (!TryExpandShortOption(flag, out string name))
                            return Error("jq: Unknown option -" + flag);
                        string? attached = null;
                        if (flag == 'L' && position + 1 < argument.Length)
                        {
                            budget.ReserveString(argument.Length - position - 1);
                            attached = argument[(position + 1)..];
                        }
                        string? error = ApplyOption(name, attached, ref i);
                        if (error is not null) return Error(error);
                        if (flag == 'L' || parsed.Help || parsed.Version || parsed.BuildConfiguration) break;
                    }
                }
                if (parsed.Help || parsed.Version || parsed.BuildConfiguration)
                    return new JqInvocation
                    {
                        StdIn = stdIn, StdOut = stdOut, StdErr = stdErr,
                        Help = parsed.Help, Version = parsed.Version, BuildConfiguration = parsed.BuildConfiguration
                    };
            }
            if (fromFile && program is null) return Error("jq: -f expects a file path");
            if (parsed.ColorOutput) return Error("jq: --color-output requires a terminal-capable host");

            // An indent of -1 selects tabs, like the reference. With no
            // formatting flag the reference pretty-prints with two spaces.
            bool useTabs = format == OutputFormat.Tabs
                || (format == OutputFormat.Spaces && parsed.Indent == -1);
            int? indent = format switch
            {
                OutputFormat.Compact => null,
                OutputFormat.Tabs => null,
                OutputFormat.Spaces => parsed.Indent is null or -1 ? null : parsed.Indent,
                _ => 2,
            };

            var filterFileArgument = fromFile ? program : null;
            var filter = fromFile ? null : program;
            // Reserve path work before expanding working-directory prefixes.
            if (filterFileArgument is not null) ChargePathResolution(filterFileArgument);
            var filterFile = filterFileArgument is null ? (JqResolvedPath?)null
                : JqPathResolution.ResolveArgument(filterFileArgument, currentDirectory);
            var libraryDirs = new List<JqResolvedPath>();
            foreach (string library in libraryPaths)
            {
                if (string.IsNullOrEmpty(library)) continue;
                ChargePathResolution(library);
                libraryDirs.Add(JqPathResolution.ResolveArgument(library, currentDirectory));
            }

            // ARGS owns argument parent links; the variable table borrows them.
            budget.ReserveMetadata(512, 8);
            var namedArguments = new JsonObject();
            var argsObject = new JsonObject { ["positional"] = positionalArguments, ["named"] = namedArguments };
            foreach (var (key, value) in variables)
            {
                budget.CheckCancellation();
                namedArguments[key] = value;
            }
            variables["ARGS"] = argsObject;
            // Intrinsic bindings stay separate from named arguments in ARGS.
            var environmentObject = new JsonObject();
            foreach (JqEnvironmentVariable variable in environment)
            {
                budget.CheckCancellation();
                if (!environmentObject.ContainsKey(variable.Name)) budget.ReserveMetadata(160, 2);
                environmentObject[variable.Name] = JsonValue.Create(variable.Value);
            }
            variables["ENV"] = environmentObject;

            return new JqInvocation
            {
                StdIn = stdIn,
                StdOut = stdOut,
                StdErr = stdErr,
                NullInput = parsed.NullInput,
                RawInput = parsed.RawInput,
                Slurp = parsed.Slurp,
                Seq = parsed.Seq,
                Stream = parsed.Stream || parsed.StreamErrors,
                StreamErrors = parsed.StreamErrors,
                RawOutput = parsed.RawOutput || parsed.JoinOutput || parsed.RawOutput0,
                JoinOutput = parsed.JoinOutput,
                RawOutput0 = parsed.RawOutput0,
                AsciiOutput = parsed.AsciiOutput,
                SortKeys = parsed.SortKeys,
                ExitStatus = parsed.ExitStatus,
                Help = parsed.Help,
                UseTabs = useTabs,
                Indent = indent,
                Version = parsed.Version,
                BuildConfiguration = parsed.BuildConfiguration,
                Filter = filter,
                FilterFile = filterFile,
                Variables = variables,
                InputFiles = operands,
                LibraryDirs = libraryDirs,
                WorkingDirectory = currentDirectory,
                Clock = clock
            };

            string? ApplyOption(string name, string? attached, ref int position)
            {
                switch (name)
                {
                    case "from-file": fromFile = true; break;
                    case "library-path":
                        if (attached is null && position + 1 >= args.Count)
                            return "-L takes a parameter: (e.g. -L /search/path or -L/search/path)";
                        budget.ReserveMetadata(64, 2);
                        libraryPaths.Add(attached ?? args[++position]);
                        break;
                    case "indent":
                        if (position + 1 >= args.Count) return "jq: --indent takes one parameter";
                        string width = args[++position];
                        if (!IsStrictIndentValue(width) || !IsIndentInRange(width))
                            return "jq: --indent takes a number between -1 and 7";
                        parsed.Indent = (width[^1] - '0') * (width[0] == '-' ? -1 : 1);
                        format = OutputFormat.Spaces;
                        break;
                    case "args": positional = true; jsonArguments = false; break;
                    case "jsonargs": positional = true; jsonArguments = true; break;
                    case "arg":
                    case "argjson":
                        if (position + 2 >= args.Count)
                            return $"jq: --{name} takes two parameters (e.g. --{name} varname "
                                + (name == "argjson" ? "text)" : "value)");
                        string variable = args[++position];
                        string value = args[++position];
                        if (!TryBindName(variable)) break;
                        if (name == "arg") variables[variable] = JsonValue.Create(value);
                        else
                        {
                            try { variables[variable] = runtime.ParseJson(value); }
                            catch (JqException exception) when (exception is not JqQuotaException)
                            { return $"jq: invalid JSON for --argjson {variable}: {exception.Message}"; }
                        }
                        break;
                    case "compact-output": format = OutputFormat.Compact; break;
                    case "tab": format = OutputFormat.Tabs; break;
                    case "version": parsed.Version = true; break;
                    case "build-configuration": parsed.BuildConfiguration = true; break;
                    case "help": parsed.Help = true; break;
                    case "null-input": parsed.NullInput = true; break;
                    case "raw-input": parsed.RawInput = true; break;
                    case "slurp": parsed.Slurp = true; break;
                    case "seq": parsed.Seq = true; break;
                    case "stream": parsed.Stream = true; break;
                    case "stream-errors": parsed.StreamErrors = true; break;
                    case "raw-output": parsed.RawOutput = true; break;
                    case "join-output": parsed.JoinOutput = true; break;
                    case "raw-output0": parsed.RawOutput0 = true; break;
                    case "ascii-output": parsed.AsciiOutput = true; break;
                    case "sort-keys": parsed.SortKeys = true; break;
                    case "color-output": parsed.ColorOutput = true; break;
                    case "exit-status": parsed.ExitStatus = true; break;
                    case "binary":
                    case "monochrome-output":
                    case "unbuffered": break;
                    default: return "jq: Unknown option --" + name;
                }
                return null;
            }

            bool TryBindName(string name)
            {
                if (boundNames.Contains(name)) return false;
                budget.ReserveMetadata(256, 4);
                boundNames.Add(name);
                return true;
            }

        }
        catch (Exception ex) when (ex is not JqQuotaException and not JqCloseException
            && ex is JqException or FormatException or JqPathException)
        {
            return Error($"jq: {ex.Message}");
        }

        JqInvocation Error(string message) => new() { StdIn = stdIn, StdOut = stdOut, StdErr = stdErr, Error = message };

        void ChargePathResolution(string path)
        {
            var maximumLength = (long)path.Length + (path.StartsWith('/') ? 0L : currentDirectory.Path.Length + 1L);
            // Reserve fixed path objects, combined/normalized strings, segment tables and
            // validation copies, including per-segment allocations for many short segments.
            budget.ReserveBytes(128 + 64 * maximumLength);
        }

    }

    private enum OutputFormat { Compact, Spaces, Tabs, Default }

    private sealed class Options
    {
        public bool Version { get; set; }
        public bool BuildConfiguration { get; set; }
        public bool NullInput { get; set; }
        public bool RawInput { get; set; }
        public bool Slurp { get; set; }
        public bool Seq { get; set; }
        public bool Stream { get; set; }
        public bool StreamErrors { get; set; }
        public bool RawOutput { get; set; }
        public bool JoinOutput { get; set; }
        public bool RawOutput0 { get; set; }
        public bool AsciiOutput { get; set; }
        public bool SortKeys { get; set; }
        public bool ColorOutput { get; set; }
        public bool ExitStatus { get; set; }
        public bool Help { get; set; }
        public int? Indent { get; set; }
    }
}
