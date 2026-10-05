using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Jq.Helpers;

namespace Lokad.Jq;

internal sealed partial class JqRuntime
{
    // Owned by one executor. General serialization remains independent because
    // filter diagnostics and tojson may serialize while an output is retained.
    internal sealed class OutputRenderer(JqRuntime runtime, JqBudget budget, JqInvocation invocation) : IDisposable
    {
        private JqJsonBuffer? _buffer;
        private Utf8JsonWriter? _writer;
        private byte[] _rawBuffer = [];

        internal ReadOnlyMemory<byte> Render(JsonNode? node)
        {
            if (invocation.RawOutput && TryGetString(node, out var text))
                return RenderRaw(text);
            JsonNode? clean = runtime.PrepareJson(node, invocation.SortKeys);
            JqJsonBuffer buffer = _buffer ??= new JqJsonBuffer(budget);
            Utf8JsonWriter writer = _writer ??= new Utf8JsonWriter(buffer,
                CreateWriterOptions(invocation.AsciiOutput, invocation.Indent, invocation.UseTabs));
            buffer.Reset();
            writer.Reset(buffer);
            ReadOnlyMemory<byte> body = runtime.WriteJson(clean, invocation.AsciiOutput,
                invocation.Indent, invocation.UseTabs, buffer, writer);
            int prefix = invocation.Seq ? 1 : 0;
            byte? terminator = invocation.RawOutput0 ? (byte)0 : invocation.JoinOutput ? null : (byte)'\n';
            int length = body.Length + prefix + (terminator.HasValue ? 1 : 0);
            budget.ChargeOutput(length);

            // Reset keeps the bytes, so the common case copies onto the same
            // span. Copy before writing RS because source and target may overlap.
            buffer.Reset();
            Span<byte> framed = buffer.GetSpan(length);
            body.Span.CopyTo(framed.Slice(prefix, body.Length));
            if (prefix != 0)
                framed[0] = 30;
            if (terminator is byte ending)
                framed[length - 1] = ending;
            buffer.Advance(length);
            return buffer.WrittenMemory;
        }

        private ReadOnlyMemory<byte> RenderRaw(string text)
        {
            if (invocation.RawOutput0 && text.Contains((char)0))
                throw new JqException("Cannot dump a string containing NUL with --raw-output0 option");
            byte? terminator = invocation.RawOutput0 ? (byte)0 : invocation.JoinOutput ? null : (byte)'\n';
            int length = Encoding.UTF8.GetByteCount(text) + (terminator.HasValue ? 1 : 0);
            // Preserve the per-record charge before growing or changing storage.
            // Exact growth needs no allowance beyond the old record allocation.
            budget.ChargeOutput(length);
            if (_rawBuffer.Length < length)
                _rawBuffer = new byte[length];
            int written = Utf8Text.Encode(text, _rawBuffer);
            if (terminator is byte ending)
                _rawBuffer[written] = ending;
            return _rawBuffer.AsMemory(0, length);
        }

        public void Dispose()
        {
            if (_writer is { } writer)
            {
                // A quota may interrupt a write with pending bytes. Cleanup
                // must discard them, not flush and throw outside the executor.
                writer.Reset();
                writer.Dispose();
            }
        }
    }
}
