namespace Lokad.Jq;

/// <summary>A borrowed current view or an owned retained capture result.</summary>
/// <remarks>
/// Cursor Current is borrowed until its next advance/disposal; RetainCurrent supplies an owner
/// and durable ranges. Owners must dispose idempotently without throwing. Default is no match.
/// </remarks>
public readonly struct JqRegexMatch(ReadOnlyMemory<JqRegexCapture> captures, IDisposable? owner) : IDisposable
{
    internal ReadOnlyMemory<JqRegexCapture> Captures { get; } = captures;
    internal IDisposable? Owner { get; } = owner;

    /// <summary>Whether the whole match participated.</summary>
    public bool Success => Captures.Length != 0 && Captures.Span[0].Success;

    /// <summary>The whole-match range; inspect Success first.</summary>
    public JqRegexCapture Value => Captures.Span[0];

    /// <summary>The actual numbered capture slots, including the whole match.</summary>
    public int CaptureSlotCount => Captures.Length;

    /// <summary>Gets a numbered capture, including an unmatched slot when present.</summary>
    public bool TryGetGroup(int index, out JqRegexCapture capture)
    {
        if ((uint)index < (uint)Captures.Length) { capture = Captures.Span[index]; return true; }
        capture = default;
        return false;
    }

    /// <summary>Ends the capture borrow and releases its provider owner.</summary>
    public void Dispose() => Owner?.Dispose();
}
