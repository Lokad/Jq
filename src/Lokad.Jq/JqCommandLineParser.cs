using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Lokad.Cli;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal static class JqCommandLineParser
{
    private static readonly char[] ShortOptions;
    private static readonly string[] LongOptions;
    private static readonly HashSet<string> BoolLongOptions = new(StringComparer.Ordinal);
    private static readonly HashSet<char> BoolShortOptions = new();

    static JqCommandLineParser()
    {
        // Lokad.Cli exposes tokenization but not the generated option names.
        // Derive them once from the same annotations instead of maintaining another list.
        var properties = typeof(JqArgs).GetProperties();
        var options = properties
            .Select(property => property.GetCustomAttribute<ArgumentAttribute>())
            .OfType<ArgumentAttribute>()
            .ToArray();
        ShortOptions = options.Select(option => option.Short).Where(name => name != '\0').ToArray();
        LongOptions = options.Select(option => option.Long).OfType<string>().ToArray();
        foreach (var property in properties)
        {
            if (property.PropertyType != typeof(bool))
                continue;
            var annotation = property.GetCustomAttribute<ArgumentAttribute>();
            if (annotation is null)
                continue;
            if (annotation.Long is string longName)
                BoolLongOptions.Add(longName);
            if (annotation.Short != (char)0)
                BoolShortOptions.Add(annotation.Short);
        }
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

            JqArgs parsed;
            OutputFormat format;
            try
            {
                parsed = ParseArguments(special.Options, out format);
            }
            catch (ParseException ex)
            {
                return Error(FormatParseError(ex.Message));
            }

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
                Help = parsed.Help,
                UseTabs = useTabs,
                Indent = indent,
                Version = parsed.Version,
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

        static JqArgs ParseArguments(IReadOnlyList<string> arguments, out OutputFormat format)
        {
            var tokens = ArgumentTokenizer.Tokenize(arguments, ShortOptions, LongOptions, false);
            var normalized = new List<ITokenizedArguments>();
            var seenBools = new HashSet<string>(StringComparer.Ordinal);
            format = OutputFormat.Default;
            while (tokens.Shift() is { } token)
            {
                // Retain token kinds: removing 'c' from a short group must not create
                // a single-dash long option such as '-version'.
                switch (token)
                {
                    case LongArg { Name: "compact-output" }:
                        format = OutputFormat.Compact;
                        continue;
                    case LongArg { Name: "tab" }:
                        format = OutputFormat.Tabs;
                        continue;
                    case LongArg { Name: "indent" }:
                        format = OutputFormat.Spaces;
                        break;
                    case LongArgWithValue withValue:
                        if (withValue.Name == "indent")
                        {
                            format = OutputFormat.Spaces;
                            if (!IsStrictIndentValue(withValue.Value) || !IsIndentInRange(withValue.Value))
                                throw new ParseException("--indent takes a number between -1 and 7");
                        }
                        break;
                    case ShortArgSet group when group.Options.Contains('c'):
                        format = OutputFormat.Compact;
                        var remaining = group.Options.Replace("c", string.Empty, StringComparison.Ordinal);
                        if (remaining.Length == 0) continue;
                        token = new ShortArgSet(remaining);
                        break;
                }

                if (token is LongArg longOption && BoolLongOptions.Contains(longOption.Name))
                {
                    if (!seenBools.Add("L:" + longOption.Name))
                        continue;
                }
                else if (token is ShortArgSet cluster)
                {
                    var kept = new StringBuilder();
                    foreach (var name in cluster.Options)
                    {
                        if (BoolShortOptions.Contains(name) && !seenBools.Add("S:" + name))
                            continue;
                        kept.Append(name);
                    }
                    if (kept.Length == 0)
                        continue;
                    if (kept.Length != cluster.Options.Length)
                        token = new ShortArgSet(kept.ToString());
                }

                normalized.Add(token);
                if (token is LongArg { Name: "indent" } indentOption)
                    PreserveOperand("--" + indentOption.Name, validateIndent: true);
                else if (token is LongArg { Name: "from-file" } fileOption)
                    PreserveOperand("--" + fileOption.Name, validateIndent: false);
                else if (token is ShortArgSet flagged)
                    foreach (var name in flagged.Options)
                        if (name == 'f') PreserveOperand("-f", validateIndent: false);
            }

            // The binder rejects duplicate booleans and loses cross-option order.
            // All operands remain for validation; emit only the final formatting flag.
            if (format == OutputFormat.Compact) normalized.Add(new LongArg("compact-output"));
            else if (format == OutputFormat.Tabs) normalized.Add(new LongArg("tab"));
            return Parser.ParseTokenized<JqArgs>(new TokenizedArguments(normalized));

            void PreserveOperand(string option, bool validateIndent)
            {
                if (tokens.Shift() is not ValueArg value)
                    throw new ParseException($"option `{option}` expects a value");
                string text = value.Value;
                if (text.StartsWith((char)0))
                    text = text[1..];
                if (validateIndent && !IsStrictIndentValue(text))
                    throw new ParseException("--indent takes a number between -1 and 7");
                normalized.Add(value.Value == text ? value : new ValueArg(text));
            }

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

    private static string FormatParseError(string message)
    {
        if (TryExtractQuotedOption(message, out var option))
            return $"jq: unsupported option {option}";
        return $"jq: {message}";
    }

    private static bool TryExtractQuotedOption(string message, out string option)
    {
        const char quote = '`';
        var start = message.IndexOf(quote);
        if (start < 0)
        {
            option = string.Empty;
            return false;
        }

        var end = message.IndexOf(quote, start + 1);
        if (end < 0)
        {
            option = string.Empty;
            return false;
        }

        option = message[(start + 1)..end];
        return option.StartsWith("-", StringComparison.Ordinal);
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
