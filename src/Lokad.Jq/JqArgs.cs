namespace Lokad.Jq;

internal sealed class JqArgs
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
    public string? FilterFile { get; set; }
    public List<string> LibraryPath { get; } = [];
}
