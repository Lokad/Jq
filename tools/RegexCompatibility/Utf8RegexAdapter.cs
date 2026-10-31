using System.Diagnostics.CodeAnalysis;
using Lokad.Jq;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Text;
using Lokad.Utf8Regex;
using Lokad.Utf8Regex.Pcre2;

namespace Lokad.Jq.Testing;

/// <summary>Temporary development adapter for the published PCRE2 0.4 package.</summary>
/// <remarks>
/// Forwards cancellation into Utf8Regex compilation and matching.
/// The provider exposes no shared memory-budget hook or compiled capture count.
/// Reflection below is pinned test/tooling compatibility, never part of the Jq package
/// or the application example. This adapter qualifies semantics only.
/// </remarks>
internal sealed class Utf8RegexAdapter : IJqRegexEngine
{
    /// <summary>The stateless application engine; compiled handles remain execution-owned.</summary>
    public static Utf8RegexAdapter Instance { get; } = new();

    /// <inheritdoc />
    public bool TryCompile(ReadOnlyMemory<byte> patternUtf8, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
        CancellationToken cancellationToken, [NotNullWhen(true)] out IJqCompiledRegex? regex, out string? error)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var flags = Pcre2CompileOptions.Ucp;
            if ((options & JqRegexCompileOptions.Caseless) != 0) flags |= Pcre2CompileOptions.Caseless;
            if ((options & JqRegexCompileOptions.Multiline) != 0) flags |= Pcre2CompileOptions.Multiline;
            if ((options & JqRegexCompileOptions.DotAll) != 0) flags |= Pcre2CompileOptions.DotAll;
            if ((options & JqRegexCompileOptions.Extended) != 0) flags |= Pcre2CompileOptions.Extended;
            var compiled = Utf8Pcre2Regex.Compile(patternUtf8.Span, flags,
                new Utf8Pcre2CompileSettings
                {
                    Newline = Pcre2NewlineConvention.Lf,
                    AllowDuplicateNames = true,
                    BackslashC = Pcre2BackslashCPolicy.Forbid,
                },
                Utf8Pcre2Regex.DefaultMatchTimeout, default, default, Timeout.InfiniteTimeSpan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var entries = new Pcre2NameEntry[compiled.NameEntryCount];
            compiled.CopyNameEntries(entries, out _);
            var names = new Dictionary<int, JqRegexGroupName>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                names[entry.Number] = new(entry.Number, Encoding.UTF8.GetBytes(entry.Name));
            }
            regex = new Compiled(compiled, names.Values.ToArray(), ReadCaptureCount(compiled));
            error = null;
            return true;
        }
        catch (Pcre2CompileException exception) when (IsResourceFailure(exception.ErrorKind))
        {
            throw new JqRegexResourceException("regex compilation failed: " + exception.Message, false);
        }
        catch (Utf8ResourceLimitExceededException exception)
        {
            throw new JqRegexResourceException("regex compilation failed: " + exception.Message, false);
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new JqRegexResourceException("regex compilation failed: " + exception.Message, false);
        }
        catch (Pcre2CompileException exception)
        {
            regex = null;
            error = "invalid regex: " + exception.Message;
            return false;
        }
        catch (NotSupportedException exception)
        {
            regex = null;
            error = "unsupported regex: " + exception.Message;
            return false;
        }
    }

    // Read the actual compiled slot table, never infer it from a pattern or a match.
    // Fail closed on any package layout change. Remove with producer qualification.
    private static int ReadCaptureCount(Utf8Pcre2Regex regex)
    {
        if (typeof(Utf8Pcre2Regex).Assembly.GetName().Version != new Version(0, 4, 0, 0))
            throw new InvalidOperationException("The temporary metadata bridge requires Pcre2 0.4.0.");
        var program = Read(regex, "DebugCompiledProgram");
        var direct = Read(Read(program, "Operations"), "Match");
        object captures = direct.GetType().Name switch
        {
            "Pcre2BacktrackingDirectProgram" => Read(direct, "Program"),
            "Pcre2SliceDirectProgram" => Read(direct, "Fallback"),
            "Pcre2SingleTokenRepeatDirectProgram" => Read(Read(direct, "Program"), "Fallback"),
            // These compiled forms are constructed only for capture-free grammar.
            "Pcre2LiteralDirectProgram" or "Pcre2CharacterDirectProgram" => 1,
            _ => throw new InvalidOperationException("Unqualified temporary capture metadata layout: " + direct.GetType().Name),
        };
        return (captures is int count ? count : (int)Read(captures, "CaptureSlotCount")) - 1;

        static object Read(object instance, string name) =>
            instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance) ?? throw new InvalidOperationException("Missing temporary metadata: " + name);
    }

    private static bool IsResourceFailure(Pcre2ErrorKind kind) => kind is
        Pcre2ErrorKind.Timeout or Pcre2ErrorKind.MatchLimit or Pcre2ErrorKind.DepthLimit or Pcre2ErrorKind.HeapLimit or Pcre2ErrorKind.ResultLimit;

    private sealed class Compiled(Utf8Pcre2Regex regex, IReadOnlyList<JqRegexGroupName> names, int captureCount) : SpanRegexCompatibility
    {
        private bool _disposed;
        public override IReadOnlyList<JqRegexGroupName> GroupNames => names;
        public override int CaptureCount => captureCount;

        public override bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try { return regex.IsMatch(input, 0, ConvertOptions(options), cancellationToken); }
            catch (Pcre2MatchException exception) { throw Failure(exception); }
            finally { cancellationToken.ThrowIfCancellationRequested(); }
        }

        public override JqRegexMatch Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var match = regex.MatchDetailed(input, start, ConvertOptions(options), cancellationToken);
                if (!match.Success) return default;
                var captures = new JqRegexCapture[match.CaptureSlotCount];
                for (int index = 0; index < match.CaptureSlotCount; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (!match.TryGetGroup(index, out var group)) break;
                    captures[index] = group.Success
                        ? new(true, group.StartOffsetInBytes, group.EndOffsetInBytes)
                        : default;
                }
                return new JqRegexMatch(captures, null);
            }
            catch (Pcre2MatchException exception) { throw Failure(exception); }
            finally { cancellationToken.ThrowIfCancellationRequested(); }
        }

        private static Pcre2MatchOptions ConvertOptions(JqRegexMatchOptions options) =>
            (options & JqRegexMatchOptions.NotEmpty) != 0 ? Pcre2MatchOptions.NotEmpty : Pcre2MatchOptions.None;

        private static Exception Failure(Pcre2MatchException exception) => IsResourceFailure(exception.ErrorKind)
            ? new JqRegexResourceException("regex matching failed: " + exception.Message, false)
            : new NotSupportedException("regex matching failed: " + exception.Message);

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
