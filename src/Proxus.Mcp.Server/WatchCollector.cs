using System.Globalization;
using Proxus.Drivers;

namespace Proxus.Mcp;

/// <summary>
///     Collects the data changes of a bounded watch: every change counts toward the summary, and the most recent
///     changes are kept in full so the model sees how the value moved.
/// </summary>
public sealed class WatchCollector
{
    public const int MaxChangesPerNode = 100;

    private readonly NodeState[] _nodes;

    public WatchCollector(IReadOnlyList<string> nodeIds)
    {
        _nodes = nodeIds.Select(static id => new NodeState(id)).ToArray();
    }

    /// <summary>Client handles are 1-based indexes into the node list.</summary>
    public static uint HandleFor(int index) => (uint)index + 1;

    public void SetMonitoredItemStatus(int index, uint statusCode)
    {
        _nodes[index].MonitoredItemStatus = statusCode;
    }

    public void Add(uint clientHandle, DriverSample sample)
    {
        if (clientHandle == 0 || clientHandle > _nodes.Length)
            return;
        _nodes[clientHandle - 1].Add(sample);
    }

    public List<WatchedNode> Results()
    {
        return _nodes.Select(static n => n.ToResult()).ToList();
    }

    private sealed class NodeState(string nodeId)
    {
        private readonly Queue<WatchChange> _recent = new();
        private int _count;
        private string? _first;
        private string? _last;
        private double? _min;
        private double? _max;
        private double _sum;
        private int _numeric;

        public uint MonitoredItemStatus { get; set; }

        public void Add(DriverSample sample)
        {
            var good = sample.Quality.Level != DriverQualityLevel.Bad;
            var text = good ? OpcUaTools.ValueText(sample.Value) : null;
            _count++;
            _first ??= text;
            _last = text;

            if (good && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
            {
                _min = _min is { } min ? Math.Min(min, number) : number;
                _max = _max is { } max ? Math.Max(max, number) : number;
                _sum += number;
                _numeric++;
            }

            _recent.Enqueue(new WatchChange(
                sample.SourceTimestampUtc == default ? null : sample.SourceTimestampUtc,
                text,
                sample.Quality.Level.ToString(),
                good ? null : OpcUaStatusCodes.Describe(sample.Quality.NativeStatusCode)?.Name));
            if (_recent.Count > MaxChangesPerNode)
                _recent.Dequeue();
        }

        public WatchedNode ToResult()
        {
            var monitored = MonitoredItemStatus == 0;
            return new WatchedNode(
                nodeId,
                monitored ? null : OpcUaStatusCodes.Describe(MonitoredItemStatus)?.Name ?? $"0x{MonitoredItemStatus:X8}",
                _count,
                _first,
                _last,
                _min,
                _max,
                _numeric > 0 ? Math.Round(_sum / _numeric, 6) : null,
                _recent.ToList(),
                _count > _recent.Count);
        }
    }
}

/// <param name="Error">Why the server refused to watch this node; null when it is being watched.</param>
/// <param name="Changes">The most recent changes, oldest first; at most <see cref="WatchCollector.MaxChangesPerNode" />.</param>
/// <param name="Truncated">More changes arrived than <paramref name="Changes" /> holds; the summary still counts them all.</param>
public sealed record WatchedNode(
    string NodeId,
    string? Error,
    int ChangeCount,
    string? First,
    string? Last,
    double? Min,
    double? Max,
    double? Average,
    List<WatchChange> Changes,
    bool Truncated);

public sealed record WatchChange(DateTime? SourceTimestampUtc, string? Value, string Quality, string? StatusName);

public sealed record WatchResult(
    string Device,
    DateTimeOffset? StartedUtc,
    double? DurationSeconds,
    double? SamplingIntervalMs,
    List<WatchedNode>? Nodes,
    ToolError? Error)
{
    /// <summary>Polled protocols only (Modbus): how many reads ran and how many failed.</summary>
    public int? Reads { get; init; }
    public int? FailedReads { get; init; }

    public static WatchResult Failed(string device, DriverError error)
    {
        return new WatchResult(device, null, null, null, null, ToolError.From(error));
    }
}
