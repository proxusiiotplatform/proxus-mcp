using System.Globalization;
using System.Text.RegularExpressions;
using Proxus.Drivers;

namespace Proxus.Mcp;

/// <summary>
///     Parses the OPC UA NodeId text form (Part 6, 5.3.1.10): <c>i=2258</c>, <c>ns=2;s=Line1.Temp</c>,
///     <c>ns=3;g=…</c>, <c>ns=1;b=base64</c>, and <c>nsu=urn:…;s=…</c>, whose namespace index only the server knows.
/// </summary>
public static partial class NodeIdText
{
    /// <summary>
    ///     Parses any form. For <c>nsu=</c> the result carries namespace index 0 and the URI in
    ///     <paramref name="namespaceUri" />; <see cref="WithNamespace" /> puts in the index the server reports.
    /// </summary>
    public static bool TryParse(string? text, out IndustrialNodeId nodeId, out string? namespaceUri, out string? error)
    {
        namespaceUri = null;
        if (text is not null && text.TrimStart().StartsWith("nsu=", StringComparison.Ordinal))
        {
            var match = NamespaceUriForm().Match(text.Trim());
            if (!match.Success)
            {
                nodeId = null!;
                error = $"'{text}': expected nsu=<namespace URI>;<type>=<value>, e.g. nsu=urn:example:plant;s=Line1.Temperature.";
                return false;
            }

            namespaceUri = match.Groups[1].Value;
            return TryParse(match.Groups[2].Value, out nodeId, out error);
        }

        return TryParse(text, out nodeId, out error);
    }

    /// <summary>The same identifier in another namespace.</summary>
    public static IndustrialNodeId WithNamespace(IndustrialNodeId nodeId, ushort namespaceIndex)
    {
        return nodeId.Kind switch
        {
            IndustrialNodeIdKind.Numeric => IndustrialNodeId.Numeric(namespaceIndex, nodeId.NumericIdentifier),
            IndustrialNodeIdKind.String => IndustrialNodeId.String(namespaceIndex, nodeId.StringIdentifier!),
            IndustrialNodeIdKind.Guid => IndustrialNodeId.Guid(namespaceIndex, nodeId.GuidIdentifier),
            _ => IndustrialNodeId.Opaque(namespaceIndex, nodeId.OpaqueIdentifier.Span)
        };
    }

    // The identifier starts at the first ";i=", ";s=", ";g=" or ";b=", so a string identifier may itself contain ";".
    [GeneratedRegex("^nsu=(.+?);([isgb]=.*)$", RegexOptions.Singleline)]
    private static partial Regex NamespaceUriForm();

    /// <summary>Parses the forms that carry a namespace index; <c>nsu=</c> needs the server and is refused here.</summary>
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
            error = $"'{text}': a namespace URI (nsu=) is resolved against the server; use a tool that reads from one.";
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
