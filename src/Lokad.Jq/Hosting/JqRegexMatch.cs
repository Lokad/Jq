namespace Lokad.Jq;

/// <summary>A provider-owned capture view, valid until disposal.</summary>
/// <remarks>The owner must dispose idempotently without throwing. Default represents no match.</remarks>
public readonly ref struct JqRegexMatch(ReadOnlySpan<JqRegexCapture> captures, IDisposable? owner)
{
    internal ReadOnlySpan<JqRegexCapture> Captures { get; } = captures;
    internal IDisposable? Owner { get; } = owner;

    /// <summary>Whether the whole match participated.</summary>
    public bool Success => Captures.Length != 0 && Captures[0].Success;

    /// <summary>The whole-match range; inspect Success first.</summary>
    public JqRegexCapture Value => Captures[0];

    /// <summary>The actual numbered capture slots, including the whole match.</summary>
    public int CaptureSlotCount => Captures.Length;

    /// <summary>Gets a numbered capture, including an unmatched slot when present.</summary>
    public bool TryGetGroup(int index, out JqRegexCapture capture)
    {
        if ((uint)index < (uint)Captures.Length) { capture = Captures[index]; return true; }
        capture = default;
        return false;
    }

    /// <summary>Ends the capture borrow and releases its provider owner.</summary>
    public void Dispose() => Owner?.Dispose();
}
