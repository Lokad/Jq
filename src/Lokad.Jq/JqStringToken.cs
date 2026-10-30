namespace Lokad.Jq;

// Shared quoted-token progress. Decoding and surrogate validation stay with
// the JSON reader. Streaming uses the boundary; DOM input also observes faults.
internal struct JqStringToken
{
    private bool _escaped;
    private int _hexDigits;

    internal readonly bool IsPlain => !_escaped && _hexDigits == 0;

    internal bool Advance(byte value, out bool closed)
    {
        closed = false;
        bool valid = value >= 32;
        if (_hexDigits != 0)
        {
            _hexDigits--;
            valid &= value is >= (byte)'0' and <= (byte)'9'
                or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
        }
        if (_escaped)
        {
            _escaped = false;
            if (value == (byte)'u') _hexDigits = 4;
            else valid &= value is (byte)'"' or (byte)'\\' or (byte)'/'
                or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t';
        }
        else if (value == (byte)'\\') _escaped = true;
        else closed = value == (byte)'"';
        return valid;
    }
}
