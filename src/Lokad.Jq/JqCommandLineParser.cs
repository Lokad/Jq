using System;
using System.Collections.Generic;
using System.Linq;
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
            var variables = new Dictionary<string, JqValue?>(StringComparer.Ordinal);
            var positionalArguments = new JqArray();
            var operands = new List<string>();
            var libraryPaths = new List<string>();
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
                        if (!jsonArguments) positionalArguments.Add(JqScalar.Create(argument, budget));
                        else
                        {
                            try { positionalArguments.Add(runtime.ParseJson(argument)); }
                            catch (JqException exception) when (exception is not JqQuotaException)
                            { return Error("jq: invalid JSON argument: ", exception.Message); }
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
                    if (argument is "--rawfile" or "--slurpfile")
                    {
                        string option = argument == "--rawfile" ? "rawfile" : "slurpfile";
                        if (i + 2 >= args.Count)
                            return Error("jq: --", option, " takes two parameters (e.g. --", option, " varname filename)");
                        string name = args[++i];
                        string path = args[++i];
                        if (!TryBindName(name)) continue;
                        try
                        {
                            var resolved = JqPathResolution.CreateRequest(path, currentDirectory, budget);
                            using var content = await host.ReadFileAsync(resolved, budget, budget.CancellationToken).ConfigureAwait(false);
                            if (option == "rawfile")
                            {
                                variables[name] = JqString.FromUtf8(content.Content.Span, budget);
                            }
                            else variables[name] = runtime.ReadJsonArray(content.Content.Span, 0);
                        }
                        catch (Exception exception) when (JqErrors.IsCatchable(exception))
                        { return Error("jq: Bad JSON in --", option, " ", name, " ", path, ": ", exception.Message); }
                    }
                    else
                    {
                        JqInvocation? error = ApplyOption(argument, null, ref i);
                        if (error is not null) return error;
                    }
                }
                else
                {
                    for (int position = 1; position < argument.Length; position++)
                    {
                        budget.CheckCancellation();
                        char flag = argument[position];
                        if (!TryExpandShortOption(flag, out string name))
                            return Error("jq: Unknown option -", flag.ToString());
                        string? attached = null;
                        if (flag == 'L' && position + 1 < argument.Length)
                        {
                            budget.ReserveString(argument.Length - position - 1);
                            attached = argument[(position + 1)..];
                        }
                        JqInvocation? error = ApplyOption(name, attached, ref i);
                        if (error is not null) return error;
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
            var filterFile = filterFileArgument is null ? (JqPathRequest?)null
                : JqPathResolution.CreateRequest(filterFileArgument, currentDirectory, budget);
            var libraryDirs = new List<JqPathRequest>();
            foreach (string library in libraryPaths)
            {
                if (string.IsNullOrEmpty(library)) continue;
                libraryDirs.Add(JqPathResolution.CreateRequest(library, currentDirectory, budget));
            }

            // ARGS owns argument values; the variable table borrows them.
            budget.ReserveMetadata(512, 8);
            var namedArguments = new JqObject(budget);
            var argsObject = new JqObject(budget) { ["positional"] = positionalArguments, ["named"] = namedArguments };
            foreach (var (key, value) in variables)
            {
                budget.CheckCancellation();
                namedArguments[key] = value;
            }
            variables["ARGS"] = argsObject;
            // Intrinsic bindings stay separate from named arguments in ARGS.
            var environmentObject = new JqObject(budget);
            using (var environmentStorage = new JqStorage(budget.MemoryBudget))
            {
                // Select winners before encoding: duplicate names retain their
                // first position and last value, without constructing discarded
                // payloads. This temporary table borrows the host's names.
                var lastEntries = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int index = 0; index < environment.Count; index++)
                {
                    budget.CheckCancellation();
                    string name = environment[index].Name;
                    if (!lastEntries.ContainsKey(name)) environmentStorage.ReserveBytes(96, 1);
                    lastEntries[name] = index;
                }
                foreach (JqEnvironmentVariable variable in environment)
                {
                    budget.CheckCancellation();
                    if (!lastEntries.Remove(variable.Name, out int last)) continue;
                    budget.ReserveMetadata(160, 2);
                    environmentObject[variable.Name] = JqString.FromHost(environment[last].Value, budget);
                }
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

            JqInvocation? ApplyOption(string argument, string? attached, ref int position)
            {
                ReadOnlySpan<char> name = argument.AsSpan(argument.StartsWith("--", StringComparison.Ordinal) ? 2 : 0);
                switch (name)
                {
                    case "from-file": fromFile = true; break;
                    case "library-path":
                        if (attached is null && position + 1 >= args.Count)
                            return Error("-L takes a parameter: (e.g. -L /search/path or -L/search/path)");
                        budget.ReserveMetadata(64, 2);
                        libraryPaths.Add(attached ?? args[++position]);
                        break;
                    case "indent":
                        if (position + 1 >= args.Count) return Error("jq: --indent takes one parameter");
                        string width = args[++position];
                        if (!TryIndent(width, out int indent))
                            return Error("jq: --indent takes a number between -1 and 7");
                        parsed.Indent = indent;
                        format = OutputFormat.Spaces;
                        break;
                    case "args": positional = true; jsonArguments = false; break;
                    case "jsonargs": positional = true; jsonArguments = true; break;
                    case "arg":
                    case "argjson":
                        if (position + 2 >= args.Count)
                            return name.SequenceEqual("argjson")
                                ? Error("jq: --argjson takes two parameters (e.g. --argjson varname text)")
                                : Error("jq: --arg takes two parameters (e.g. --arg varname value)");
                        string variable = args[++position];
                        string value = args[++position];
                        if (!TryBindName(variable)) break;
                        if (name.SequenceEqual("arg")) variables[variable] = JqScalar.Create(value, budget);
                        else
                        {
                            try { variables[variable] = runtime.ParseJson(value); }
                            catch (JqException exception) when (exception is not JqQuotaException)
                            { return Error("jq: invalid JSON for --argjson ", variable, ": ", exception.Message); }
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
                    default: return Error("jq: Unknown option ", argument);
                }
                return null;
            }

            bool TryBindName(string name)
            {
                if (variables.ContainsKey(name)) return false;
                budget.ReserveMetadata(192, 3);
                return true;
            }

            // One pass over optional sign and ASCII digits, with bounded
            // arithmetic even for arbitrarily many borrowed leading zeros.
            bool TryIndent(string text, out int indent)
            {
                budget.CheckCancellation();
                indent = 0;
                if (text.Length == 0) return false;
                bool negative = text[0] == '-';
                int start = text[0] is '+' or '-' ? 1 : 0;
                if (start == text.Length) return false;
                int value = 0;
                for (int index = start; index < text.Length; index++)
                {
                    if ((index & 4095) == 0) budget.CheckCancellation();
                    if (!char.IsAsciiDigit(text[index])) return false;
                    value = value * 10 + text[index] - '0';
                    if (value > (negative ? 1 : 7)) return false;
                }
                budget.CheckCancellation();
                indent = negative ? -value : value;
                return true;
            }
        }
        catch (Exception ex) when (ex is not JqQuotaException and not JqCloseException
            && ex is JqException or FormatException)
        {
            return Error("jq: ", ex.Message);
        }

        JqInvocation Error(params string[] parts)
        {
            string message;
            if (parts.Length == 1) message = parts[0];
            else
            {
                long length = 0;
                foreach (string part in parts) length += part.Length;
                budget.ReserveString(length);
                budget.CheckCancellation();
                message = string.Concat(parts);
                budget.CheckCancellation();
            }
            return new() { StdIn = stdIn, StdOut = stdOut, StdErr = stdErr, Error = message };
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
