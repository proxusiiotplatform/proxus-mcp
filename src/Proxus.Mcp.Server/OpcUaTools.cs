using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Proxus.Drivers;
using Proxus.Drivers.OpcUa;

namespace Proxus.Mcp;

/// <summary>Read-only OPC UA tools. Nothing here can write, call methods or change subscriptions on a server.</summary>
[McpServerToolType]
public sealed class OpcUaTools(DeviceSessions sessions)
{
    private const int MaxNodes = 50;
    private const int MaxReferences = 200;
    private const string ObjectsFolder = "i=85";

    [McpServerTool(Name = "browse", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists the children of a node on an OPC UA device: node id, browse name, display name, node class and type. Start without node_id (the Objects folder) and walk down; Variable nodes can then be read with read_nodes.")]
    public async Task<CallToolResult> Browse(
        [Description("Device name from list_devices.")] string device,
        [Description("NodeId to browse, e.g. i=85 (Objects), ns=2;s=Line1. Defaults to the Objects folder.")] string? nodeId = null,
        [Description("Maximum number of children to return (1-200).")] int max = 100,
        CancellationToken cancellationToken = default)
    {
        var text = string.IsNullOrWhiteSpace(nodeId) ? ObjectsFolder : nodeId;
        if (!NodeIdText.TryParse(text, out var parsed, out var parseError))
            return Fail(BrowseResult.Failed(device, text, Invalid(parseError!)));
        if (max is < 1 or > MaxReferences)
            return Fail(BrowseResult.Failed(device, text, Invalid($"max must be between 1 and {MaxReferences}.")));

        var session = await sessions.GetOpcUaAsync(device, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
            return Fail(BrowseResult.Failed(device, text, session.Error));

        var browse = await session.Value.BrowseAsync(
            new OpcUaBrowseDescription(parsed, NodeClassMask: OpcUaNodeClass.Object | OpcUaNodeClass.Variable | OpcUaNodeClass.Method),
            (uint)max,
            cancellationToken).ConfigureAwait(false);
        if (!browse.IsSuccess)
            return Fail(BrowseResult.Failed(device, text, browse.Error));

        var children = browse.Value.References
            .Where(static r => r.IsForward)
            .Take(max)
            .Select(static r => new BrowseChild(
                r.NodeId.NodeId.ToString(),
                r.BrowseName.ToString(),
                r.DisplayName.Text,
                r.NodeClass.ToString(),
                r.TypeDefinition?.NodeId.ToString()))
            .ToList();

        return ToolOutput.Ok(
            new BrowseResult(device, parsed.ToString(), children, browse.Value.HasContinuationPoint || children.Count == max, null),
            Json.BrowseResult);
    }

    [McpServerTool(Name = "read_nodes", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads the current value of up to 50 OPC UA Variable nodes in one request. Returns each value with its data type, quality (Good, Uncertain, Bad), the OPC UA status code and the source timestamp. Find node ids with browse.")]
    public async Task<CallToolResult> ReadNodes(
        [Description("Device name from list_devices.")] string device,
        [Description("NodeIds to read, e.g. [\"ns=2;s=Line1.Temperature\", \"i=2258\"].")] string[] nodeIds,
        CancellationToken cancellationToken = default)
    {
        if (nodeIds is null || nodeIds.Length is 0 or > MaxNodes)
            return Fail(ReadNodesResult.Failed(device, Invalid($"node_ids must hold between 1 and {MaxNodes} NodeIds.")));

        var descriptors = new OpcUaNodeDescriptor[nodeIds.Length];
        for (var i = 0; i < nodeIds.Length; i++)
        {
            if (!NodeIdText.TryParse(nodeIds[i], out var parsed, out var parseError))
                return Fail(ReadNodesResult.Failed(device, Invalid(parseError!)));
            descriptors[i] = new OpcUaNodeDescriptor(parsed);
        }

        var session = await sessions.GetOpcUaAsync(device, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
            return Fail(ReadNodesResult.Failed(device, session.Error));

        var samples = new DriverSample[descriptors.Length];
        var read = await session.Value.ReadAsync(descriptors, samples, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Fail(ReadNodesResult.Failed(device, read.Error));

        var values = new List<NodeValue>(descriptors.Length);
        for (var i = 0; i < descriptors.Length; i++)
        {
            var sample = samples[i];
            var good = sample.Quality.Level != DriverQualityLevel.Bad;
            values.Add(new NodeValue(
                descriptors[i].NodeId.ToString(),
                good ? sample.Value.ToInvariantString() : null,
                good ? sample.Value.Type.ToString() : null,
                sample.Quality.Level.ToString(),
                $"0x{sample.Quality.NativeStatusCode:X8}",
                OpcUaStatusCodes.Describe(sample.Quality.NativeStatusCode)?.Name,
                sample.SourceTimestampUtc == default ? null : sample.SourceTimestampUtc));
        }

        return ToolOutput.Ok(new ReadNodesResult(device, DateTimeOffset.UtcNow, values, null), Json.ReadNodesResult);
    }

    private static ToolJsonContext Json => ToolJsonContext.Readable;

    private static CallToolResult Fail(BrowseResult result)
    {
        return ToolOutput.Failed(result, Json.BrowseResult);
    }

    private static CallToolResult Fail(ReadNodesResult result)
    {
        return ToolOutput.Failed(result, Json.ReadNodesResult);
    }

    private static DriverError Invalid(string message)
    {
        return new DriverError(DriverErrorKind.Configuration, "INVALID_ARGUMENT", message);
    }
}

public sealed record BrowseChild(string NodeId, string BrowseName, string? DisplayName, string NodeClass, string? TypeDefinition);

public sealed record BrowseResult(string Device, string NodeId, List<BrowseChild>? Children, bool? MayHaveMore, ToolError? Error)
{
    public static BrowseResult Failed(string device, string nodeId, DriverError error)
    {
        return new BrowseResult(device, nodeId, null, null, ToolError.From(error));
    }
}

public sealed record NodeValue(
    string NodeId,
    string? Value,
    string? DataType,
    string Quality,
    string StatusCode,
    string? StatusName,
    DateTime? SourceTimestampUtc);

public sealed record ReadNodesResult(string Device, DateTimeOffset? TimestampUtc, List<NodeValue>? Values, ToolError? Error)
{
    public static ReadNodesResult Failed(string device, DriverError error)
    {
        return new ReadNodesResult(device, null, null, ToolError.From(error));
    }
}
