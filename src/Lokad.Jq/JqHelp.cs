using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Lokad.Jq;

// Truthful command help: every listed option is implemented by this
// embedding runtime. Deliberately divergent defaults and no-op
// acceptances are marked inline rather than omitted.
internal static class JqHelp
{
    internal const string Text =
        "Lokad jq - embeddable jq command with host-mediated IO.\n" +
        "\n" +
        "Usage: jq [options] filter [files...]\n" +
        "\n" +
        "Inputs:\n" +
        "  -n, --null-input          use `null` as the single input value;\n" +
        "  -R, --raw-input           read each line as a string instead of JSON;\n" +
        "  -s, --slurp               read all inputs into an array and use it as\n" +
        "                            the single input value;\n" +
        "  -f, --from-file           load the filter from a file;\n" +
        "  -L, --library-path dir    search modules from the directory;\n" +
        "      --arg name value      set $name to the string value;\n" +
        "      --argjson name value  set $name to the JSON value;\n" +
        "      --slurpfile name file set $name to an array of JSON values read\n" +
        "                            from the file;\n" +
        "      --rawfile name file   set $name to string contents of file;\n" +
        "      --args                consume remaining arguments as positional\n" +
        "                            string values;\n" +
        "      --jsonargs            consume remaining arguments as positional\n" +
        "                            JSON values;\n" +
        "      --                    terminates argument processing;\n" +
        "\n" +
        "Output:\n" +
        "  -c, --compact-output      compact instead of pretty-printed output;\n" +
        "  -r, --raw-output          output strings without escapes and quotes;\n" +
        "      --raw-output0         implies -r and output NUL after each output;\n" +
        "  -j, --join-output         implies -r and output without newline after\n" +
        "                            each output;\n" +
        "  -a, --ascii-output        output strings by only ASCII characters\n" +
        "                            using escape sequences;\n" +
        "  -S, --sort-keys           sort keys of each object on output;\n" +
        "  -M, --monochrome-output   accepted; output is never colorized;\n" +
        "      --tab                 use tabs for indentation;\n" +
        "      --indent n            use n spaces for indentation (-1 selects\n" +
        "                            tabs, max 7 spaces);\n" +
        "      --unbuffered          accepted; outputs already stream one value\n" +
        "                            at a time with host backpressure;\n" +
        "      --seq                 parse input and frame output as\n" +
        "                            application/json-seq;\n" +
        "  -e, --exit-status         set exit status from the last output values\n" +
        "                            (4 when there is no output, 1 when the last\n" +
        "                            values are false or null, else 0);\n" +
        "\n" +
        "Streaming:\n" +
        "      --stream              parse the input in streaming fashion;\n" +
        "      --stream-errors       implies --stream and report parse errors as\n" +
        "                            an array;\n" +
        "\n" +
        "Introspection:\n" +
        "  -V, --version             show the version;\n" +
        "      --build-configuration show build configuration;\n" +
        "  -h, --help                show this help;\n" +
        "\n" +
        "Named arguments are also available as $ARGS.named[], while\n" +
        "positional arguments are available as $ARGS.positional[].\n";
}

// Truthful local build configuration assembled from the running assembly:
// identity, framework, regex engine, and embedding constraints. This never
// claims the upstream `jq-1.8.2` identity or C build flags.
internal static class JqBuildConfiguration
{
    internal static string Text
    {
        get
        {
            string version = typeof(Jq).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0";
            return $"Lokad jq {version} ({RuntimeInformation.FrameworkDescription}; PCRE.NET regex; hosted IO)";
        }
    }
}
