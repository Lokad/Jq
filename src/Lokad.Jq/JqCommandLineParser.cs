using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    // Range check for strict `--indent` values without integer overflow:
    // only -1..7 are in range, so any multi-digit significant is out of range
    // (this also covers values far past int range, which must report the
    // reference diagnostic instead of tripping binder conversion errors).
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

    public static JqInvocation Parse(
        IReadOnlyList<string> args,
        JqPath currentDirectory,
        JqFileDescriptor stdIn,
        JqFileDescriptor stdOut,
        JqFileDescriptor stdErr,
        IReadOnlyList<JqEnvironmentVariable> environment,
        JqClock? clock)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var budget = new JqBudget(CancellationToken.None);
        var runtime = new JqRuntime(budget);
        try
        {
            if (args.Count > 4096)
                throw new JqException("argument count exceeds the 4096 limit");
            foreach (var argument in args)
                budget.ChargeInput(Encoding.UTF8.GetByteCount(argument));
            var special = ExtractSpecialArguments(args, runtime);
            if (special.Error != null)
                return Error(special.Error);

            if (!TryParseArguments(special.Options, out JqArgs parsed, out OutputFormat format,
                    out bool helpFirst, out string parseError))
                return Error(parseError);

            if (parsed.Indent is < -1 or > 7)
                return Error("jq: --indent takes a number between -1 and 7");

            if (parsed.ColorOutput)
                return Error("jq: --color-output requires a terminal-capable host");

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

            var operands = special.Operands;
            var filterFileArgument = special.FromFile ?? parsed.FilterFile;
            var filter = filterFileArgument == null && operands.Count > 0 ? operands[0] : null;
            var fileArgumentStart = filterFileArgument == null && operands.Count > 0 ? 1 : 0;
            // Reserve all path work before expanding any working-directory prefixes.
            for (var i = fileArgumentStart; i < operands.Count; i++)
                ChargePathResolution(operands[i]);
            if (filterFileArgument != null) ChargePathResolution(filterFileArgument);
            var inputFiles = operands
                .Skip(fileArgumentStart)
                .Select(path => JqPathResolution.ResolveArgument(path, currentDirectory))
                .ToList();
            var filterFile = filterFileArgument == null
                ? (JqResolvedPath?)null
                : JqPathResolution.ResolveArgument(filterFileArgument, currentDirectory);
            var libraryDirs = new List<JqResolvedPath>();
            foreach (string library in special.LibraryDirs.Concat(parsed.LibraryPath))
            {
                if (string.IsNullOrEmpty(library))
                    continue;
                ChargePathResolution(library);
                libraryDirs.Add(JqPathResolution.ResolveArgument(library, currentDirectory));
            }

            var namedArguments = new JsonObject();
            var argsObject = new JsonObject { ["positional"] = new JsonArray(special.Positional.Select(runtime.Clone).ToArray()), ["named"] = namedArguments };
            foreach (var (key, value) in special.Variables)
                namedArguments[key] = runtime.Clone(value);
            special.Variables["ARGS"] = argsObject;
            // Snapshot exported variables for $ENV unless the caller bound the name explicitly.
            if (!special.Variables.ContainsKey("ENV"))
            {
                var environmentObject = new JsonObject();
                foreach (JqEnvironmentVariable variable in environment)
                {
                    budget.ChargeNode();
                    budget.ChargeString(variable.Name.Length + variable.Value.Length);
                    environmentObject[variable.Name] = JsonValue.Create(variable.Value);
                }
                special.Variables["ENV"] = environmentObject;
            }

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
                Help = parsed.Help && (!parsed.Version || helpFirst),
                UseTabs = useTabs,
                Indent = indent,
                Version = parsed.Version && (!parsed.Help || !helpFirst),
                BuildConfiguration = parsed.BuildConfiguration,
                Filter = filter,
                FilterFile = filterFile,
                Variables = special.Variables,
                PositionalArguments = special.Positional,
                InputFiles = inputFiles,
                FileVariables = special.FileVariables,
                LibraryDirs = libraryDirs,
                WorkingDirectory = currentDirectory,
                Clock = clock
            };
        }
        catch (Exception ex) when (ex is JqException or FormatException or JqPathException)
        {
            return Error($"jq: {ex.Message}");
        }

        JqInvocation Error(string message) => new() { StdIn = stdIn, StdOut = stdOut, StdErr = stdErr, Error = message };

        void ChargePathResolution(string path)
        {
            var maximumLength = (long)path.Length + (path.StartsWith('/') ? 0L : currentDirectory.Path.Length + 1L);
            // Reserve fixed path objects, combined/normalized strings, segment tables and
            // validation copies, including per-segment allocations for many short segments.
            budget.ChargeBytes(128 + 64 * maximumLength);
        }

        static bool TryParseArguments(IReadOnlyList<string> arguments, out JqArgs parsed,
            out OutputFormat format, out bool helpFirst, out string error)
        {
            var result = new JqArgs();
            parsed = result;
            format = OutputFormat.Default;
            helpFirst = true;
            error = string.Empty;
            var selectedFormat = OutputFormat.Default;
            var seenBools = new Dictionary<string, string>(StringComparer.Ordinal);
            int order = 0;
            int helpAt = int.MaxValue;
            int versionAt = int.MaxValue;
            bool optionsDone = false;

            // Numeric short options fail during classification, before binding.
            // Negative operands outside this option list were already extracted.
            foreach (string argument in arguments)
            {
                if (argument == "--") break;
                if (argument.Length > 1 && argument[0] == '-' && char.IsAsciiDigit(argument[1]))
                {
                    error = "jq: Unknown option -" + argument[1];
                    return false;
                }
            }

            for (int i = 0; i < arguments.Count; i++)
            {
                string argument = arguments[i];
                if (optionsDone || argument == "--") break;
                if (argument.Length < 2 || argument[0] != '-')
                    continue;
                bool doubleDash = argument[1] == '-';
                int nameStart = doubleDash ? 2 : 1;
                int equals = argument.IndexOf('=', nameStart);
                string name = equals < 0 ? argument[nameStart..] : argument[nameStart..equals];
                string? inlineValue = equals < 0 ? null : argument[(equals + 1)..];
                // Retain the existing accepted single-dash long aliases. A short
                // cluster stays a cluster even when removing c would spell one.
                if (doubleDash || IsKnownLongOption(name))
                {
                    if (!TryApplyOption(name, "--" + name,
                            inlineValue, ref i, out error))
                        return false;
                    continue;
                }

                bool validCluster = equals < 0 && name.Length > 0;
                foreach (char flag in name)
                    validCluster &= TryExpandShortOption(flag, out _);
                if (!validCluster)
                {
                    // A leading value-taking short may own its attached operand.
                    // Mixed clusters with an unknown flag keep their existing diagnostic.
                    if (name.Length > 0 && name[0] is 'f' or 'L')
                    {
                        TryExpandShortOption(name[0], out string valueOption);
                        string value = inlineValue ?? name[1..];
                        if (!TryApplyOption(valueOption, "-" + name[0], value, ref i, out error))
                            return false;
                        continue;
                    }
                    string head = name.Length == 0 ? argument : "-" + name[0];
                    error = "jq: Unknown option " + ResolveClusterFlag(head, arguments);
                    return false;
                }
                foreach (char flag in name)
                {
                    TryExpandShortOption(flag, out string option);
                    if (!TryApplyOption(option, "-" + flag, null, ref i, out error))
                        return false;
                }
            }
            format = selectedFormat;
            helpFirst = helpAt <= versionAt;
            return true;

            bool TryApplyOption(string name, string label, string? inlineValue,
                ref int position, out string diagnostic)
            {
                diagnostic = string.Empty;
                if (name is "indent" or "from-file" or "library-path")
                {
                    string value;
                    if (inlineValue is not null)
                        value = inlineValue;
                    else if (position + 2 < arguments.Count && arguments[position + 1] == "--")
                    {
                        position += 2;
                        value = arguments[position];
                        optionsDone = true;
                    }
                    else if (position + 1 < arguments.Count
                             && (arguments[position + 1].Length == 0
                                 || arguments[position + 1][0] != '-'
                                 || arguments[position + 1] == "-"))
                        value = arguments[++position];
                    else
                    {
                        diagnostic = "jq: Unknown option " + ResolveClusterFlag(label, arguments);
                        return false;
                    }
                    if (value.StartsWith((char)0)) value = value[1..];
                    if (name == "indent")
                    {
                        if (!IsStrictIndentValue(value) || !IsIndentInRange(value))
                        {
                            diagnostic = "jq: --indent takes a number between -1 and 7";
                            return false;
                        }
                        // Range was checked without conversion, even for arbitrarily
                        // many leading zeros. Only the final digit and sign matter.
                        result.Indent = value[^1] - '0';
                        if (value[0] == '-') result.Indent = -result.Indent;
                        selectedFormat = OutputFormat.Spaces;
                    }
                    else if (name == "from-file") result.FilterFile = value;
                    else result.LibraryPath.Add(value);
                    return true;
                }
                if (inlineValue is not null || !IsKnownLongOption(name))
                {
                    diagnostic = "jq: Unknown option " + ResolveClusterFlag(label, arguments);
                    return false;
                }
                if (name == "compact-output")
                {
                    selectedFormat = OutputFormat.Compact;
                    return true;
                }
                if (name == "tab")
                {
                    selectedFormat = OutputFormat.Tabs;
                    return true;
                }
                if (name == "help") helpAt = Math.Min(helpAt, order++);
                if (name == "version") versionAt = Math.Min(versionAt, order++);
                // Preserve the existing binder's disposition for mixed aliases;
                // repeated occurrences of the same spelling remain idempotent.
                if (seenBools.TryGetValue(name, out string? previous) && previous != label)
                {
                    diagnostic = "jq: Unknown option " + ResolveClusterFlag(label, arguments);
                    return false;
                }
                seenBools[name] = label;
                switch (name)
                {
                    case "version": result.Version = true; break;
                    case "build-configuration": result.BuildConfiguration = true; break;
                    case "null-input": result.NullInput = true; break;
                    case "raw-input": result.RawInput = true; break;
                    case "slurp": result.Slurp = true; break;
                    case "seq": result.Seq = true; break;
                    case "stream": result.Stream = true; break;
                    case "stream-errors": result.StreamErrors = true; break;
                    case "raw-output": result.RawOutput = true; break;
                    case "join-output": result.JoinOutput = true; break;
                    case "raw-output0": result.RawOutput0 = true; break;
                    case "ascii-output": result.AsciiOutput = true; break;
                    case "sort-keys": result.SortKeys = true; break;
                    case "color-output": result.ColorOutput = true; break;
                    case "exit-status": result.ExitStatus = true; break;
                    case "help": result.Help = true; break;
                    // The accepted binary, monochrome and unbuffered flags are inert.
                }
                return true;
            }

            static bool IsKnownLongOption(string name) => name is
                "version" or "build-configuration" or "unbuffered" or "binary"
                or "null-input" or "raw-input" or "slurp" or "seq" or "stream"
                or "stream-errors" or "compact-output" or "raw-output" or "join-output"
                or "raw-output0" or "ascii-output" or "sort-keys" or "color-output"
                or "monochrome-output" or "exit-status" or "help" or "tab" or "indent"
                or "from-file" or "library-path";
        }
    }

    private static SpecialArguments ExtractSpecialArguments(IReadOnlyList<string> args, JqRuntime runtime)
    {
        var variables = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var positional = new List<JsonNode?>();
        var options = new List<string>();
        string? fromFile = null;
        var fileVariables = new List<(string Name, string Path, bool Raw)>();
        var libraryDirs = new List<string>();
        bool argsDone = false;
        // Mirror the reference parser: --args/--jsonargs only set a mode for
        // later non-option operands. Options keep parsing after markers, the
        // first non-option operand stays the filter (or, with -f, every operand
        // stays a file candidate), pre-marker operands stay input files, and --
        // only stops option parsing without clearing the positional mode.
        bool furtherStrings = false;
        bool furtherJson = false;
        var operandModes = new List<(string Text, bool Positional, bool AsJson)>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!argsDone && arg == "--")
            {
                argsDone = true;
                continue;
            }

            if (!argsDone && (arg == "--args" || arg == "--jsonargs"))
            {
                furtherStrings = arg == "--args";
                furtherJson = arg == "--jsonargs";
                continue;
            }

            if (!argsDone && arg == "--arg")
            {
                if (i + 2 >= args.Count)
                    return SpecialArguments.Failure("jq: --arg expects name and value");
                var name = args[++i];
                var value = args[++i];
                if (!variables.ContainsKey(name) && !fileVariables.Any(entry => entry.Name == name))
                    variables[name] = JsonValue.Create(value);
                continue;
            }

            if (!argsDone && arg == "--argjson")
            {
                if (i + 2 >= args.Count)
                    return SpecialArguments.Failure("jq: --argjson expects name and JSON value");
                var name = args[++i];
                if (variables.ContainsKey(name) || fileVariables.Any(entry => entry.Name == name))
                {
                    i++;
                    continue;
                }
                try { variables[name] = runtime.ParseJson(args[++i]); }
                catch (JqException ex) { return SpecialArguments.Failure($"jq: invalid JSON for --argjson {name}: {ex.Message}"); }
                continue;
            }

            if (!argsDone && (arg == "--rawfile" || arg == "--slurpfile"))
            {
                bool raw = arg == "--rawfile";
                string which = raw ? "rawfile" : "slurpfile";
                if (i + 2 >= args.Count)
                    return SpecialArguments.Failure($"jq: --{which} takes two parameters (e.g. --{which} varname filename)");
                var name = args[++i];
                var path = args[++i];
                if (!variables.ContainsKey(name) && !fileVariables.Any(entry => entry.Name == name))
                    fileVariables.Add((name, path, raw));
                continue;
            }

            if (!argsDone && (arg == "-f" || arg == "--from-file"))
            {
                if (i + 1 >= args.Count)
                    return SpecialArguments.Failure("jq: -f expects a file path");
                if (fromFile is null)
                    fromFile = args[++i];
                continue;
            }

            if (!argsDone && arg == "--indent")
            {
                if (i + 1 >= args.Count)
                    return SpecialArguments.Failure("jq: --indent takes one parameter");
                var indentText = args[++i];
                if (!IsStrictIndentValue(indentText) || !IsIndentInRange(indentText))
                    return SpecialArguments.Failure("--indent takes a number between -1 and 7");
                options.Add(arg);
                // Negative values would retokenize as numeric options, so
                // smuggle them past the tokenizer with a sentinel prefix
                // that the indent operand handler strips back off.
                options.Add(indentText.StartsWith('-') ? "\0" + indentText : indentText);
                continue;
            }

            if (!argsDone && arg == "-L")
            {
                if (i + 1 >= args.Count)
                    return SpecialArguments.Failure("-L takes a parameter: (e.g. -L /search/path or -L/search/path)");
                libraryDirs.Add(args[++i]);
                continue;
            }

            if (!argsDone && arg.StartsWith("-L", StringComparison.Ordinal) && arg.Length > 2 && arg[1] != '-')
            {
                libraryDirs.Add(arg[2..]);
                continue;
            }

            if (!argsDone && arg == "--library-path")
            {
                if (i + 1 >= args.Count)
                    return SpecialArguments.Failure("-L takes a parameter: (e.g. -L /search/path or -L/search/path)");
                libraryDirs.Add(args[++i]);
                continue;
            }

            if (!argsDone && arg.StartsWith("--library-path=", StringComparison.Ordinal))
            {
                libraryDirs.Add(arg["--library-path=".Length..]);
                continue;
            }

            if (!argsDone && arg.Length > 2 && arg[0] == '-' && char.IsAsciiLetter(arg[1]))
            {
                // Single-dash clusters: boolean shorts stay grouped for the
                // binder, `-L` claims its remainder or the next argument, and
                // clusters holding `-f` keep the legacy grouped path where
                // each `-f` consumes the next argument with last-wins.
                var kept = new System.Text.StringBuilder();
                kept.Append('-');
                bool legacy = false;
                for (int position = 1; position < arg.Length; position++)
                {
                    char name = arg[position];
                    if (name == 'L')
                    {
                        if (position + 1 < arg.Length)
                        {
                            libraryDirs.Add(arg[(position + 1)..]);
                        }
                        else
                        {
                            if (i + 1 >= args.Count)
                                return SpecialArguments.Failure("-L takes a parameter: (e.g. -L /search/path or -L/search/path)");
                            libraryDirs.Add(args[++i]);
                        }
                        break;
                    }
                    if (name == 'f')
                    {
                        legacy = true;
                        break;
                    }
                    if (char.IsAsciiLetter(name))
                    {
                        kept.Append(name);
                        continue;
                    }
                    legacy = true;
                    break;
                }
                if (!legacy)
                {
                    if (kept.Length > 1)
                        options.Add(kept.ToString());
                    continue;
                }
                options.Add(arg);
                int following = 0;
                foreach (char name in arg)
                {
                    if (name == 'f')
                        following++;
                }
                for (int j = 0; j < following && i + 1 < args.Count; j++)
                    options.Add(args[++i]);
                continue;
            }

            if (!argsDone && arg.StartsWith("--from-file=", StringComparison.Ordinal))
            {
                fromFile ??= arg["--from-file=".Length..];
                continue;
            }

            if (!argsDone && Isoptish(arg))
            {
                options.Add(arg);
                continue;
            }

            operandModes.Add((arg, furtherStrings || furtherJson, furtherJson));
        }

        // Split operands like the reference: the first non-option operand stays the
        // filter unless the program comes from -f, pre-marker operands stay input
        // files, and each post-marker operand uses the marker active when it was seen.
        var operands = new List<string>();
        bool programSeen = fromFile is not null;
        foreach (var entry in operandModes)
        {
            if (!programSeen)
            {
                programSeen = true;
                operands.Add(entry.Text);
                continue;
            }

            if (!entry.Positional)
            {
                operands.Add(entry.Text);
                continue;
            }

            if (!entry.AsJson)
            {
                positional.Add(JsonValue.Create(entry.Text));
                continue;
            }

            try { positional.Add(runtime.ParseJson(entry.Text)); }
            catch (JqException ex) { return SpecialArguments.Failure($"jq: invalid JSON argument: {ex.Message}"); }
        }

        return new SpecialArguments(options, operands, variables, positional, fromFile, fileVariables, libraryDirs, null);
    }

    // Upstream names the failing flag inside dash clusters (`-cZ` reports
    // `-Z`) while the binder quotes the cluster head. Walk the letters of
    // the failing cluster like getopt: known boolean shorts combine, and
    // the first other letter is the failure unless a value-taking short
    // claims the rest, in which case the binder wording stands.
    private static string ResolveClusterFlag(string option, IReadOnlyList<string> options)
    {
        if (option.Length != 2 || option[0] != '-' || !char.IsAsciiLetter(option[1]))
            return option;
        string? cluster = null;
        foreach (string argument in options)
        {
            if (argument.Length > 2 && argument.StartsWith(option, StringComparison.Ordinal)
                && argument[1] != '-' && IsAsciiLetters(argument[2..]))
            {
                cluster = argument;
                break;
            }
        }
        cluster ??= options.Contains(option) ? option : null;
        if (cluster is null || cluster.Length < 3)
            return option;
        foreach (char flag in cluster.AsSpan(1))
        {
            if (TryExpandShortOption(flag, out string name))
            {
                if (name is "from-file" or "library-path") return option;
                continue;
            }
            return "-" + flag;
        }
        return option;
    }

    private static bool IsAsciiLetters(string text)
    {
        if (text.Length == 0)
            return false;
        foreach (char flag in text)
        {
            if (!char.IsAsciiLetter(flag))
                return false;
        }
        return true;
    }

    private enum OutputFormat { Compact, Spaces, Tabs, Default }

    private sealed record SpecialArguments(
        IReadOnlyList<string> Options,
        IReadOnlyList<string> Operands,
        Dictionary<string, JsonNode?> Variables,
        IReadOnlyList<JsonNode?> Positional,
        string? FromFile,
        IReadOnlyList<(string Name, string Path, bool Raw)> FileVariables,
        IReadOnlyList<string> LibraryDirs,
        string? Error)
    {
        public static SpecialArguments Failure(string error) => new(
            [], [], new Dictionary<string, JsonNode?>(StringComparer.Ordinal), [], null, [], [], error);
    }

}
