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

    static JqCommandLineParser()
    {
        // Lokad.Cli exposes tokenization but not the generated option names.
        // Derive them once from the same annotations instead of maintaining another list.
        var options = typeof(JqArgs).GetProperties()
            .Select(property => property.GetCustomAttribute<ArgumentAttribute>())
            .OfType<ArgumentAttribute>()
            .ToArray();
        ShortOptions = options.Select(option => option.Short).Where(name => name != '\0').ToArray();
        LongOptions = options.Select(option => option.Long).OfType<string>().ToArray();
    }

    public static JqInvocation Parse(
        IReadOnlyList<string> args,
        JqPath currentDirectory,
        JqFileDescriptor stdIn,
        JqFileDescriptor stdOut,
        JqFileDescriptor stdErr)
    {
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
            try
            {
                parsed = ParseArguments(special.Arguments);
            }
            catch (ParseException ex)
            {
                return Error(FormatParseError(ex.Message));
            }

            if (parsed.Indent is < 0 or > 8)
                return Error("jq: --indent expects an integer from 0 to 8");

            var filterArguments = parsed.Arguments;
            var filterFileArgument = parsed.FilterFile;
            var filter = filterFileArgument == null && filterArguments.Length > 0 ? filterArguments[0] : null;
            var fileArgumentStart = filterFileArgument == null && filterArguments.Length > 0 ? 1 : 0;
            // Reserve all path work before expanding any working-directory prefixes.
            for (var i = fileArgumentStart; i < filterArguments.Length; i++)
                ChargePathResolution(filterArguments[i]);
            if (filterFileArgument != null) ChargePathResolution(filterFileArgument);
            var inputFiles = filterArguments
                .Skip(fileArgumentStart)
                .Select(path => JqPathResolution.ResolveArgument(path, currentDirectory))
                .ToList();
            var filterFile = filterFileArgument == null
                ? (JqResolvedPath?)null
                : JqPathResolution.ResolveArgument(filterFileArgument, currentDirectory);

            var namedArguments = new JsonObject();
            var argsObject = new JsonObject { ["positional"] = new JsonArray(special.Positional.Select(runtime.Clone).ToArray()), ["named"] = namedArguments };
            foreach (var (key, value) in special.Variables)
                namedArguments[key] = runtime.Clone(value);
            special.Variables["ARGS"] = argsObject;

            return new JqInvocation
            {
                StdIn = stdIn,
                StdOut = stdOut,
                StdErr = stdErr,
                NullInput = parsed.NullInput,
                RawInput = parsed.RawInput,
                Slurp = parsed.Slurp,
                RawOutput = parsed.RawOutput,
                JoinOutput = parsed.JoinOutput,
                AsciiOutput = parsed.AsciiOutput,
                UseTabs = parsed.UseTabs,
                Indent = parsed.CompactOutput || parsed.UseTabs ? null : parsed.Indent,
                Version = parsed.Version,
                BuildConfiguration = parsed.BuildConfiguration,
                Filter = filter,
                FilterFile = filterFile,
                Variables = special.Variables,
                PositionalArguments = special.Positional,
                InputFiles = inputFiles
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

        static JqArgs ParseArguments(IReadOnlyList<string> arguments)
        {
            var tokens = ArgumentTokenizer.Tokenize(arguments, ShortOptions, LongOptions, false);
            var normalized = new List<ITokenizedArguments>();
            var format = OutputFormat.Compact;
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
                    case LongArgWithValue { Name: "indent" }:
                        format = OutputFormat.Spaces;
                        break;
                    case ShortArgSet group when group.Options.Contains('c'):
                        format = OutputFormat.Compact;
                        var remaining = group.Options.Replace("c", string.Empty, StringComparison.Ordinal);
                        if (remaining.Length == 0) continue;
                        token = new ShortArgSet(remaining);
                        break;
                }

                normalized.Add(token);
                if (token is LongArg { Name: "indent" or "from-file" } option)
                    PreserveOperand("--" + option.Name);
                else if (token is ShortArgSet group)
                    foreach (var name in group.Options)
                        if (name == 'f') PreserveOperand("-f");
            }

            // The binder rejects duplicate booleans and loses cross-option order.
            // All operands remain for validation; emit only the final formatting flag.
            if (format == OutputFormat.Compact) normalized.Add(new LongArg("compact-output"));
            else if (format == OutputFormat.Tabs) normalized.Add(new LongArg("tab"));
            return Parser.ParseTokenized<JqArgs>(new TokenizedArguments(normalized));

            void PreserveOperand(string option)
            {
                if (tokens.Shift() is not ValueArg value)
                    throw new ParseException($"option `{option}` expects a value");
                normalized.Add(value);
            }
        }
    }

    private static SpecialArguments ExtractSpecialArguments(IReadOnlyList<string> args, JqRuntime runtime)
    {
        var variables = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var positional = new List<JsonNode?>();
        var remaining = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                for (; i < args.Count; i++)
                    remaining.Add(args[i]);
                break;
            }

            if (arg == "--args" || arg == "--jsonargs")
            {
                var jsonArgs = arg == "--jsonargs";
                for (i++; i < args.Count; i++)
                {
                    if (!jsonArgs)
                    {
                        positional.Add(JsonValue.Create(args[i]));
                        continue;
                    }

                    try { positional.Add(runtime.ParseJson(args[i])); }
                    catch (JqException ex) { return SpecialArguments.Failure($"jq: invalid JSON argument: {ex.Message}"); }
                }
                break;
            }

            if (arg == "--arg")
            {
                if (i + 2 >= args.Count)
                    return SpecialArguments.Failure("jq: --arg expects name and value");
                variables[args[++i]] = JsonValue.Create(args[++i]);
                continue;
            }

            if (arg == "--argjson")
            {
                if (i + 2 >= args.Count)
                    return SpecialArguments.Failure("jq: --argjson expects name and JSON value");
                var name = args[++i];
                try { variables[name] = runtime.ParseJson(args[++i]); }
                catch (JqException ex) { return SpecialArguments.Failure($"jq: invalid JSON for --argjson {name}: {ex.Message}"); }
                continue;
            }

            if (arg == "-f" || arg == "--from-file")
            {
                if (i + 1 >= args.Count)
                    return SpecialArguments.Failure("jq: -f expects a file path");
                remaining.Add("--from-file");
                remaining.Add(args[++i]);
                continue;
            }

            if (arg.StartsWith("--from-file=", StringComparison.Ordinal))
            {
                remaining.Add("--from-file");
                remaining.Add(arg["--from-file=".Length..]);
                continue;
            }

            remaining.Add(arg);
        }

        return new SpecialArguments(remaining, variables, positional, null);
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

    private enum OutputFormat { Compact, Spaces, Tabs }

    private sealed record SpecialArguments(
        IReadOnlyList<string> Arguments,
        Dictionary<string, JsonNode?> Variables,
        IReadOnlyList<JsonNode?> Positional,
        string? Error)
    {
        public static SpecialArguments Failure(string error) => new([], new Dictionary<string, JsonNode?>(StringComparer.Ordinal), [], error);
    }
}
