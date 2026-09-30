using Lokad.Cli;

namespace Lokad.Jq;

[Arguments("Processes JSON input with a jq-compatible filter.")]
internal partial record JqArgs
{
    [Argument("Print version.", Short = 'V', Long = "version")] public bool Version { get; init; }
    [Argument("Print build configuration.", Long = "build-configuration")] public bool BuildConfiguration { get; init; }
    [Argument("Accepted for compatibility.", Long = "unbuffered")] public bool Unbuffered { get; init; }
    [Argument("Accepted for compatibility; byte streams are already binary-safe.", Short = 'b', Long = "binary")] public bool Binary { get; init; }
    [Argument("Use null as input.", Short = 'n', Long = "null-input")] public bool NullInput { get; init; }
    [Argument("Read input as raw strings.", Short = 'R', Long = "raw-input")] public bool RawInput { get; init; }
    [Argument("Slurp inputs into one value.", Short = 's', Long = "slurp")] public bool Slurp { get; init; }
    [Argument("Read JSON text sequences.", Long = "seq")] public bool Seq { get; init; }
    [Argument("Read inputs as a stream of path and leaf events.", Long = "stream")] public bool Stream { get; init; }
    [Argument("Recover from streaming input errors as events.", Long = "stream-errors")] public bool StreamErrors { get; init; }
    [Argument("Write compact JSON output.", Short = 'c', Long = "compact-output")] public bool CompactOutput { get; init; }
    [Argument("Write raw strings.", Short = 'r', Long = "raw-output")] public bool RawOutput { get; init; }
    [Argument("Implies raw output without newlines.", Short = 'j', Long = "join-output")] public bool JoinOutput { get; init; }
    [Argument("Implies raw output with NUL terminators.", Long = "raw-output0")] public bool RawOutput0 { get; init; }
    [Argument("Escape non-ASCII output.", Short = 'a', Long = "ascii-output")] public bool AsciiOutput { get; init; }
    [Argument("Sort object keys on output.", Short = 'S', Long = "sort-keys")] public bool SortKeys { get; init; }
    [Argument("Colorize output; needs a terminal-capable host.", Short = 'C', Long = "color-output")] public bool ColorOutput { get; init; }
    [Argument("Accepted for compatibility; output is never colorized.", Short = 'M', Long = "monochrome-output")] public bool Monochrome { get; init; }
    [Argument("Exit with status from the last output values.", Short = 'e', Long = "exit-status")] public bool ExitStatus { get; init; }
    [Argument("Show command help.", Short = 'h', Long = "help")] public bool Help { get; init; }
    [Argument("Indent with tabs.", Long = "tab")] public bool UseTabs { get; init; }
    [Argument("Pretty-print indentation width.", Long = "indent")] public int? Indent { get; init; }
    [Argument("Read filter from file.", Short = 'f', Long = "from-file")] public string? FilterFile { get; init; }
    [Argument("Module library search directories.", Short = 'L', Long = "library-path")] public string[] LibraryPath { get; init; } = [];
    [Argument("Filter and input file arguments.", Name = "argument")] public string[] Arguments { get; init; } = [];
}
