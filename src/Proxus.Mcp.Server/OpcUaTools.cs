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
    private const int MaxWatchedNodes = 20;
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

    [McpServerTool(Name = "watch_nodes", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Watches up to 20 OPC UA Variable nodes for a few seconds through an OPC UA subscription and returns every change the server reported: per node the change count, first and last value, min, max and average for numbers, and the most recent changes with source timestamps. Use it to see how values move (trends, spikes, a machine starting or stopping) rather than a single reading. The subscription is closed when the call returns.")]
    public async Task<CallToolResult> WatchNodes(
        [Description("Device name from list_devices.")] string device,
        [Description("NodeIds to watch, e.g. [\"ns=3;i=1001\"]. Find them with browse.")] string[] nodeIds,
        [Description("How long to watch, in seconds (1-60).")] int durationSeconds = 10,
        [Description("How often the server samples each value, in milliseconds (100-10000). The server may revise it.")] int samplingIntervalMs = 500,
        CancellationToken cancellationToken = default)
    {
        if (nodeIds is null || nodeIds.Length is 0 or > MaxWatchedNodes)
            return Fail(WatchResult.Failed(device, Invalid($"nodeIds must hold between 1 and {MaxWatchedNodes} NodeIds.")));
        if (durationSeconds is < 1 or > 60)
            return Fail(WatchResult.Failed(device, Invalid("durationSeconds must be between 1 and 60.")));
        if (samplingIntervalMs is < 100 or > 10_000)
            return Fail(WatchResult.Failed(device, Invalid("samplingIntervalMs must be between 100 and 10000.")));

        var items = new OpcUaMonitoredItemRequest[nodeIds.Length];
        for (var i = 0; i < nodeIds.Length; i++)
        {
            if (!NodeIdText.TryParse(nodeIds[i], out var parsed, out var parseError))
                return Fail(WatchResult.Failed(device, Invalid(parseError!)));
            items[i] = new OpcUaMonitoredItemRequest(new OpcUaNodeDescriptor(parsed), WatchCollector.HandleFor(i),
                SamplingInterval: samplingIntervalMs, QueueSize: 10);
        }

        var session = await sessions.GetOpcUaAsync(device, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
            return Fail(WatchResult.Failed(device, session.Error));
        var client = session.Value;

        // Publish at least once a second, and send keep-alives often enough that the loop below notices the deadline.
        var publishingInterval = Math.Min(samplingIntervalMs, 1000);
        var keepAlive = (uint)Math.Max(1, 1000 / publishingInterval);
        var subscription = await client.CreateSubscriptionAsync(
            new OpcUaSubscriptionRequest(publishingInterval, keepAlive * 10, keepAlive), cancellationToken).ConfigureAwait(false);
        if (!subscription.IsSuccess)
            return Fail(WatchResult.Failed(device, subscription.Error));

        var subscriptionId = subscription.Value.SubscriptionId;
        var collector = new WatchCollector(items.Select(static i => i.Node.NodeId.ToString()).ToList());
        var started = DateTimeOffset.UtcNow;
        try
        {
            var created = await client.CreateMonitoredItemsAsync(subscriptionId, items, cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess)
                return Fail(WatchResult.Failed(device, created.Error));
            for (var i = 0; i < created.Value.Length; i++)
                collector.SetMonitoredItemStatus(i, created.Value[i].StatusCode);

            var deadline = started.AddSeconds(durationSeconds);
            var acknowledgements = new List<OpcUaSubscriptionAcknowledgement>();
            while (DateTimeOffset.UtcNow < deadline)
            {
                var publish = await client.PublishAsync(acknowledgements.ToArray(), cancellationToken).ConfigureAwait(false);
                if (!publish.IsSuccess)
                    return Fail(WatchResult.Failed(device, publish.Error));
                acknowledgements.Clear();
                if (!publish.Value.IsKeepAlive)
                    acknowledgements.Add(new OpcUaSubscriptionAcknowledgement(publish.Value.SubscriptionId, publish.Value.SequenceNumber));
                if (publish.Value.SubscriptionId != subscriptionId)
                    continue;
                foreach (var value in publish.Value.Values)
                    collector.Add(value.ClientHandle, value.Sample);
            }
        }
        finally
        {
            // A subscription left behind keeps sampling on the server until its lifetime runs out.
            await client.DeleteSubscriptionsAsync(new[] { subscriptionId }, CancellationToken.None).ConfigureAwait(false);
        }

        return ToolOutput.Ok(
            new WatchResult(device, started, Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1), samplingIntervalMs, collector.Results(), null),
            Json.WatchResult);
    }

    private static ToolJsonContext Json => ToolJsonContext.Readable;

    private static CallToolResult Fail(WatchResult result)
    {
        return ToolOutput.Failed(result, Json.WatchResult);
    }

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
