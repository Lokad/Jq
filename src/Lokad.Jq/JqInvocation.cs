using System.Collections.Generic;
using System.Text.Json.Nodes;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal sealed class JqInvocation
{
    public JqFileDescriptor StdIn { get; init; }
    public JqFileDescriptor StdOut { get; init; }
    public JqFileDescriptor StdErr { get; init; }
    public bool NullInput { get; init; }
    public bool RawInput { get; init; }
    public bool Slurp { get; init; }
    public bool RawOutput { get; init; }
    public bool JoinOutput { get; init; }
    public bool AsciiOutput { get; init; }
    public bool UseTabs { get; init; }
    public int? Indent { get; init; }
    public bool Version { get; init; }
    public bool BuildConfiguration { get; init; }
    public string? Filter { get; init; }
    public JqResolvedPath? FilterFile { get; init; }
    public IReadOnlyDictionary<string, JsonNode?> Variables { get; init; } = new Dictionary<string, JsonNode?>();
    public IReadOnlyList<JsonNode?> PositionalArguments { get; init; } = [];
    public IReadOnlyList<JqResolvedPath> InputFiles { get; init; } = [];
    public string? Error { get; init; }
}
