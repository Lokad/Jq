using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq;

/// <summary>A borrowed regex engine. Implementations own cooperation inside compilation and searches.</summary>
/// <remarks>
/// Forward the supplied cancellation token and shared budget into the engine. Do not create
/// an independent allowance. Refused admission must throw JqRegexResourceException with
/// IsMemoryAdmission set. Cancellation must propagate as OperationCanceledException.
/// The host owns this engine; each successful compilation transfers a handle to Jq.
/// Jq owns the supplied pattern text through the handle's lifetime. Its cache-key
/// storage is already charged; implementations admit their own compiled storage.
/// </remarks>
public interface IJqRegexEngine
{
    /// <summary>Compiles a pattern, or returns false and a catchable syntax/unsupported diagnostic.</summary>
    bool TryCompile(string pattern, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
        CancellationToken cancellationToken, [NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error);
}

/// <summary>A compiled handle owned by an execution until its last subject/result borrower finishes.</summary>
/// <remarks>Dispose must be idempotent and non-throwing. Input and capture spans are borrowed only during calls.</remarks>
public interface IJqCompiledRegex : IDisposable
{
    /// <summary>An upper bound on numbered capture slots, including the whole match.</summary>
    int CaptureSlotCapacity { get; }

    /// <summary>Names by capture number, stable until disposal.</summary>
    IReadOnlyDictionary<int, string> GroupNames { get; }

    /// <summary>Tests the complete UTF-8 subject without materializing captures.</summary>
    bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken);

    /// <summary>Searches from an absolute byte offset and fills numbered captures; returns zero for no match.</summary>
    /// <remarks>
    /// Retain complete-input anchor/lookbehind context. Offsets are absolute in the original
    /// UTF-8/UTF-16 subject; unmatched slots have Success=false. The destination has at least
    /// CaptureSlotCapacity slots. Return the actual slot count, including slot zero.
    /// </remarks>
    int Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options,
        Span<JqRegexCapture> captures, CancellationToken cancellationToken);
}

/// <summary>Engine-neutral compilation flags used by jq modifiers.</summary>
[Flags]
public enum JqRegexCompileOptions
{
    /// <summary>Default matching.</summary>
    None = 0,
    /// <summary>Case-insensitive matching.</summary>
    Caseless = 1,
    /// <summary>Line-aware anchors.</summary>
    Multiline = 2,
    /// <summary>Dot includes newlines.</summary>
    DotAll = 4,
    /// <summary>Extended pattern syntax.</summary>
    Extended = 8,
}

/// <summary>Engine-neutral search flags.</summary>
[Flags]
public enum JqRegexMatchOptions
{
    /// <summary>Default matching.</summary>
    None = 0,
    /// <summary>Suppress empty matches.</summary>
    NotEmpty = 1,
}

/// <summary>A numbered capture's participation and absolute byte/UTF-16 bounds.</summary>
public readonly record struct JqRegexCapture(bool Success, int StartOffsetInBytes,
    int EndOffsetInBytes, int StartOffsetInUtf16, int EndOffsetInUtf16);

/// <summary>A terminal engine resource failure, distinct from unsupported syntax and cancellation.</summary>
public sealed class JqRegexResourceException(string message, bool isMemoryAdmission) : Exception(message)
{
    /// <summary>Whether shared storage admission was refused.</summary>
    public bool IsMemoryAdmission { get; } = isMemoryAdmission;
}
