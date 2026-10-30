namespace Lokad.Jq;

/// <summary>A host lookup retaining the original path spelling and caller's current directory.</summary>
/// <remarks>
/// The host resolves relative paths and owns link traversal, permissions and confinement.
/// Dot segments, repeated separators and trailing slashes are not normalized by execution.
/// </remarks>
public sealed record JqPathRequest
{
    /// <summary>Initializes an unresolved lookup. Absolute paths ignore the current directory during lookup.</summary>
    public JqPathRequest(string path, JqPath currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        Path = path;
        CurrentDirectory = currentDirectory;
    }

    /// <summary>Gets the original path text, including traversal and trailing-slash intent.</summary>
    public string Path { get; }

    /// <summary>Gets the caller's explicit current directory.</summary>
    public JqPath CurrentDirectory { get; }
}
