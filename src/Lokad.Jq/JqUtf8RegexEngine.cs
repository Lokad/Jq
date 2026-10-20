using System.Diagnostics.CodeAnalysis;
using Lokad.Utf8Regex.Pcre2;

namespace Lokad.Jq;

/// <summary>The default managed PCRE2 regex adapter.</summary>
/// <remarks>
/// Checks cancellation around synchronous Utf8Regex 0.3.0 calls;
/// an active search must finish before cancellation is observed.
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
        var memory = new JqMemoryReservation(memoryBudget);
        bool retained = false;
        try
        {
            // One owner covers the conservative compiled representation, metadata and
            // pooled engine workspace. Jq separately owns subject/capture scratch.
            memory.Resize(320 * 1024L + 64L * pattern.Length);
            var flags = Pcre2CompileOptions.Ucp;
            if ((options & JqRegexCompileOptions.Caseless) != 0) flags |= Pcre2CompileOptions.Caseless;
            if ((options & JqRegexCompileOptions.Multiline) != 0) flags |= Pcre2CompileOptions.Multiline;
            if ((options & JqRegexCompileOptions.DotAll) != 0) flags |= Pcre2CompileOptions.DotAll;
            if ((options & JqRegexCompileOptions.Extended) != 0) flags |= Pcre2CompileOptions.Extended;
            var compiled = new Utf8Pcre2Regex(pattern, flags,
                new Utf8Pcre2CompileSettings
                {
                    Newline = Pcre2NewlineConvention.Lf,
                    AllowDuplicateNames = true,
                    BackslashC = Pcre2BackslashCPolicy.Forbid,
                },
                default, Utf8Pcre2Regex.DefaultMatchTimeout);
            cancellationToken.ThrowIfCancellationRequested();
            if (compiled.NameEntryCount != 0)
                memory.Resize(checked(memory.LastAdmittedBytes + 64L * compiled.NameEntryCount));
            var entries = new Pcre2NameEntry[compiled.NameEntryCount];
            compiled.CopyNameEntries(entries, out _);
            var names = new Dictionary<int, string>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                names[entry.Number] = entry.Name;
            }
            int captureSlots = (int)Math.Min(int.MaxValue, 1L + pattern.Length);
            regex = new Compiled(compiled, names, captureSlots, memory);
            error = null;
            retained = true;
            return true;
        }
        catch (JqQuotaException exception) when (exception.IsMemoryAdmission)
        {
            throw new JqRegexResourceException("memory budget exceeded", true);
        }
        catch (Pcre2CompileException exception) when (IsResourceFailure(exception.ErrorKind))
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
        finally
        {
            if (!retained) memory.Dispose();
        }
    }

    private static bool IsResourceFailure(Pcre2ErrorKind kind) => kind is
        Pcre2ErrorKind.Timeout or Pcre2ErrorKind.MatchLimit or Pcre2ErrorKind.DepthLimit or Pcre2ErrorKind.HeapLimit;

    private sealed class Compiled(Utf8Pcre2Regex regex, IReadOnlyDictionary<int, string> names,
        int captureSlots, JqMemoryReservation memory) : IJqCompiledRegex
    {
        private bool _disposed;
        public int CaptureSlotCapacity => captureSlots;
        public IReadOnlyDictionary<int, string> GroupNames => names;

        public bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try { return regex.IsMatch(input, 0, ConvertOptions(options)); }
            catch (Pcre2MatchException exception) { throw Failure(exception); }
            finally { cancellationToken.ThrowIfCancellationRequested(); }
        }

        public int Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options,
            Span<JqRegexCapture> captures, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var match = regex.MatchDetailed(input, start, ConvertOptions(options));
                if (!match.Success) return 0;
                for (int index = 0; index < match.CaptureSlotCount; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (!match.TryGetGroup(index, out var group)) break;
                    captures[index] = group.Success
                        ? new(true, group.StartOffsetInBytes, group.EndOffsetInBytes,
                            group.StartOffsetInUtf16, group.EndOffsetInUtf16)
                        : default;
                }
                return match.CaptureSlotCount;
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
            memory.Dispose();
        }
    }
}
