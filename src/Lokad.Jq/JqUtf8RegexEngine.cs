using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Lokad.Utf8Regex;
using Lokad.Utf8Regex.Pcre2;

namespace Lokad.Jq;

/// <summary>The default managed PCRE2 regex adapter.</summary>
/// <remarks>
/// Forwards cancellation into Utf8Regex compilation and matching.
/// The provider exposes no shared memory-budget hook.
/// </remarks>
public sealed class JqUtf8RegexEngine : IJqRegexEngine
{
    /// <summary>The stateless default engine; compiled handles remain execution-owned.</summary>
    public static JqUtf8RegexEngine Instance { get; } = new();

    /// <inheritdoc />
    public bool TryCompile(string pattern, JqRegexCompileOptions options, IMemoryBudget? memoryBudget,
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
            var compiled = Utf8Pcre2Regex.Compile(pattern, flags,
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
            var names = new Dictionary<int, string>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                names[entry.Number] = entry.Name;
            }
            regex = new Compiled(compiled, names);
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

    private static bool IsResourceFailure(Pcre2ErrorKind kind) => kind is
        Pcre2ErrorKind.Timeout or Pcre2ErrorKind.MatchLimit or Pcre2ErrorKind.DepthLimit or Pcre2ErrorKind.HeapLimit or Pcre2ErrorKind.ResultLimit;

    private sealed class Compiled(Utf8Pcre2Regex regex, IReadOnlyDictionary<int, string> names) : IJqCompiledRegex
    {
        private bool _disposed;
        public IReadOnlyDictionary<int, string> GroupNames => names;

        public bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try { return regex.IsMatch(input, 0, ConvertOptions(options), cancellationToken); }
            catch (Pcre2MatchException exception) { throw Failure(exception); }
            finally { cancellationToken.ThrowIfCancellationRequested(); }
        }

        public JqRegexMatch Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options,
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
                        ? new(true, group.StartOffsetInBytes, group.EndOffsetInBytes,
                            group.StartOffsetInUtf16, group.EndOffsetInUtf16)
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
            : new JqException("regex matching failed: " + exception.Message);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
