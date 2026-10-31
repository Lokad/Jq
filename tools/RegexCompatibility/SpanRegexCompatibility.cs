using Lokad.Jq;

namespace Lokad.Jq.Testing;

// Temporary test/tool bridge for span-based providers and existing failure fixtures.
// It deliberately keeps old-engine allocation costs visible. It is neither packaged
// nor linked into the application example, and does not qualify provider admission.
internal abstract class SpanRegexCompatibility : IJqCompiledRegex
{
    public abstract int CaptureCount { get; }
    public abstract IReadOnlyList<JqRegexGroupName> GroupNames { get; }
    public abstract bool IsMatch(ReadOnlySpan<byte> input, JqRegexMatchOptions options, CancellationToken cancellationToken);
    public abstract JqRegexMatch Match(ReadOnlySpan<byte> input, int start, JqRegexMatchOptions options, CancellationToken cancellationToken);
    public abstract void Dispose();

    public IJqRegexSubject Prepare(ReadOnlyMemory<byte> subject, IMemoryBudget? memoryBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new Subject(this, subject);
    }

    private sealed class Subject(SpanRegexCompatibility compiled, ReadOnlyMemory<byte> bytes) : IJqRegexSubject
    {
        private bool _disposed;
        public bool IsMatch(JqRegexMatchOptions options, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return compiled.IsMatch(bytes.Span, options, cancellationToken);
        }

        public IJqRegexCursor CreateCursor(JqRegexMatchOptions options, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return new Cursor(compiled, bytes, options);
        }

        public void Dispose() => _disposed = true;
    }

    private sealed class Cursor(SpanRegexCompatibility compiled, ReadOnlyMemory<byte> bytes,
        JqRegexMatchOptions options) : IJqRegexCursor
    {
        private int _start;
        private bool _finished;
        private bool _disposed;
        private JqRegexMatch _current;
        public JqRegexMatch Current => _current.Success
            ? _current : throw new InvalidOperationException("No current regex match.");

        public bool MoveNext(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _current.Dispose();
            _current = default;
            if (_finished) return false;
            _current = compiled.Match(bytes.Span, _start, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_current.Success) { _finished = true; return false; }
            var range = _current.Value;
            _start = range.EndOffsetInBytes;
            if (range.StartOffsetInBytes == range.EndOffsetInBytes)
            {
                if (_start == bytes.Length) _finished = true;
                else
                {
                    _start++;
                    while (_start < bytes.Length && (bytes.Span[_start] & 0xc0) == 0x80) _start++;
                }
            }
            return true;
        }

        public JqRegexMatch RetainCurrent(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var retained = Current;
            var borrowed = new JqRegexCapture[retained.CaptureSlotCount];
            for (int index = 0; index < borrowed.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                retained.TryGetGroup(index, out borrowed[index]);
            }
            // Transfer any provider result owner; keep a separate current view.
            _current = new JqRegexMatch(borrowed, null);
            return retained;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Dispose();
            _current = default;
        }
    }
}
