namespace Lokad.Jq;

/// <summary>
/// Represents a resolved file descriptor handle.
/// </summary>
public readonly record struct JqFileDescriptor
{
    /// <summary>Initializes a descriptor from a nonnegative host-defined identifier.</summary>
    public JqFileDescriptor(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        Id = id;
    }

    /// <summary>Gets the host-defined descriptor identifier.</summary>
    public int Id { get; }

    /// <summary>Gets the conventional standard-input descriptor.</summary>
    public static JqFileDescriptor StdIn => new(0);

    /// <summary>Gets the conventional standard-output descriptor.</summary>
    public static JqFileDescriptor StdOut => new(1);

    /// <summary>Gets the conventional standard-error descriptor.</summary>
    public static JqFileDescriptor StdErr => new(2);

    /// <summary>Gets the distinguished descriptor that discards writes.</summary>
    public static JqFileDescriptor DevNull => new(int.MaxValue);

    /// <summary>Gets the canonical path corresponding to the null device.</summary>
    public static JqPath DevNullPath { get; } = new("/dev/null");

    /// <summary>Gets whether this is the distinguished null-device descriptor.</summary>
    public bool IsDevNull => this == DevNull;
}
