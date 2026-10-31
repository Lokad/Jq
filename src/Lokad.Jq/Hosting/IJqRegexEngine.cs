using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq;

/// <summary>A borrowed regex engine. Implementations own cooperation inside compilation and searches.</summary>
/// <remarks>
/// Forward the supplied cancellation token and shared budget into the engine. Do not create
/// an independent allowance. Refused admission must throw JqRegexResourceException with
/// IsMemoryAdmission set. Cancellation must propagate as OperationCanceledException.
/// The host owns this engine; each successful compilation transfers a handle to Jq.
/// Jq owns immutable supplied pattern bytes through the handle's lifetime. Its cache-key
/// storage is already charged. Providers own syntax validation, resource bounds,
/// and admission for compilation, workspace and capture results.
/// </remarks>
public interface IJqRegexEngine
{
    /// <summary>Compiles a pattern, or returns false and a catchable syntax/unsupported diagnostic.</summary>
    bool TryCompile(ReadOnlyMemory<byte> patternUtf8, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
        CancellationToken cancellationToken, [NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error);
}

/// <summary>A compiled handle owned by an execution until its last subject/result borrower finishes.</summary>
/// <remarks>Dispose must be idempotent and non-throwing. Active subjects/results finish before disposal.</remarks>
public interface IJqCompiledRegex : IDisposable
{
    /// <summary>Gets the actual number of numbered capture slots, excluding whole-match slot zero.</summary>
    int CaptureCount { get; }

    /// <summary>Immutable UTF-8 names by capture number, borrowed through handle disposal.</summary>
    IReadOnlyList<JqRegexGroupName> GroupNames { get; }

    /// <summary>Prepares an owned operation over borrowed, immutable, already-funded UTF-8 bytes.</summary>
    /// <remarks>
    /// Keep the bytes and their owner valid through subject disposal and all active cursor/result
    /// borrowers. Admit actual substantial operation storage against the supplied shared budget;
    /// null selects execution without shared accounting. Do not retain the creation token as the
    /// authority for later calls. Preserve complete-input anchors and lookbehind context.
    /// </remarks>
    IJqRegexSubject Prepare(ReadOnlyMemory<byte> subject, IMemoryBudget? memoryBudget,
        CancellationToken cancellationToken);
}

/// <summary>An owned prepared subject. Disposal is idempotent and non-throwing.</summary>
public interface IJqRegexSubject : IDisposable
{
    /// <summary>Tests existence without materializing captures, using the current call's token.</summary>
    bool IsMatch(JqRegexMatchOptions options, CancellationToken cancellationToken);

    /// <summary>Creates an owned forward cursor over the complete subject.</summary>
    /// <remarks>
    /// After an empty match, advance one Unicode scalar. An empty match at the end is final.
    /// The cursor borrows this subject until cursor disposal. Each advance receives its current token.
    /// </remarks>
    IJqRegexCursor CreateCursor(JqRegexMatchOptions options, CancellationToken cancellationToken);
}

/// <summary>An owned incremental search, safe to retain across asynchronous suspension.</summary>
public interface IJqRegexCursor : IDisposable
{
    /// <summary>Advances using this call's cancellation token, returning whether a match was found.</summary>
    bool MoveNext(CancellationToken cancellationToken);

    /// <summary>Gets borrowed capture ranges, valid only until the next advance or cursor disposal.</summary>
    /// <remarks>Read after a successful advance. Include slot zero and nonparticipating slots.</remarks>
    JqRegexMatch Current { get; }

    /// <summary>Retains the current capture ranges independently of later advances or cursor disposal.</summary>
    /// <remarks>The result owner keeps its storage and subject/program borrowers funded until disposal.</remarks>
    JqRegexMatch RetainCurrent(CancellationToken cancellationToken);
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

/// <summary>A numbered capture's participation and absolute UTF-8 byte bounds.</summary>
public readonly record struct JqRegexCapture(bool Success, int StartOffsetInBytes,
    int EndOffsetInBytes);

/// <summary>An immutable UTF-8 capture name and its numbered slot, borrowed from the compiled handle.</summary>
public readonly record struct JqRegexGroupName(int GroupNumber, ReadOnlyMemory<byte> NameUtf8);

/// <summary>A terminal engine resource failure, distinct from unsupported syntax and cancellation.</summary>
public sealed class JqRegexResourceException(string message, bool isMemoryAdmission) : Exception(message)
{
    /// <summary>Whether shared storage admission was refused.</summary>
    public bool IsMemoryAdmission { get; } = isMemoryAdmission;
}
