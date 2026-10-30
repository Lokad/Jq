
namespace Lokad.Jq;

internal static class JqRecursion
{
    internal const int MaximumDepth = 64;

    internal static void CheckDepth(int depth, string message)
    {
        if (depth > MaximumDepth)
            throw new JqQuotaException(message);
    }

    // Containers consume a level; scalar leaves at the boundary remain valid.
    internal static void CheckValueDepth(JqValue? value, int depth)
    {
        if (depth > MaximumDepth || depth == MaximumDepth && value is JqArray or JqObject)
            throw new JqQuotaException("value nesting limit exceeded");
    }
}
