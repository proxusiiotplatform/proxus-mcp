using System.Globalization;
using Proxus.Drivers;

namespace Proxus.Mcp;

/// <summary>
///     Parses the OPC UA NodeId text form (Part 6, 5.3.1.10): <c>i=2258</c>, <c>ns=2;s=Line1.Temp</c>,
///     <c>ns=3;g=…</c>, <c>ns=1;b=base64</c>. Namespace URIs (<c>nsu=</c>) are not supported yet.
/// </summary>
public static class NodeIdText
{
    public static bool TryParse(string? text, out IndustrialNodeId nodeId, out string? error)
    {
        nodeId = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "NodeId is empty.";
            return false;
        }

        var span = text.AsSpan().Trim();
        ushort namespaceIndex = 0;
        if (span.StartsWith("ns=", StringComparison.Ordinal))
        {
            var semicolon = span.IndexOf(';');
            if (semicolon < 0 || !ushort.TryParse(span[3..semicolon], NumberStyles.None, CultureInfo.InvariantCulture, out namespaceIndex))
            {
                error = $"'{text}': expected ns=<index>;<type>=<value>, e.g. ns=2;s=Line1.Temperature.";
                return false;
            }

            span = span[(semicolon + 1)..];
        }
        else if (span.StartsWith("nsu=", StringComparison.Ordinal))
        {
            error = $"'{text}': namespace URIs (nsu=) are not supported yet; use the namespace index (ns=).";
            return false;
        }

        if (span.Length < 2 || span[1] != '=')
        {
            error = $"'{text}': identifier must start with i=, s=, g= or b=.";
            return false;
        }

        var value = span[2..];
        switch (span[0])
        {
            case 'i' when ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric):
                nodeId = IndustrialNodeId.Numeric(namespaceIndex, numeric);
                return true;
            case 's' when !value.IsEmpty:
                nodeId = IndustrialNodeId.String(namespaceIndex, value.ToString());
                return true;
            case 'g' when Guid.TryParse(value, out var guid):
                nodeId = IndustrialNodeId.Guid(namespaceIndex, guid);
                return true;
            case 'b':
                try
                {
                    nodeId = IndustrialNodeId.Opaque(namespaceIndex, Convert.FromBase64String(value.ToString()));
                    return true;
                }
                catch (FormatException)
                {
                    break;
                }
        }

        error = $"'{text}': invalid {span[0]}= identifier.";
        return false;
    }
}
