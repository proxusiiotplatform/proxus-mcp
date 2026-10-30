using Proxus.Drivers;
using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

public class WatchCollectorTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private static DriverSample Good(double value, int second) =>
        DriverSample.Good(DriverValue.FromFloat64(value), T0.AddSeconds(second), T0.AddSeconds(second));

    [Fact]
    public void Summarises_numeric_changes_per_node()
    {
        var collector = new WatchCollector(["ns=3;i=1004", "ns=3;i=1001"]);
        collector.Add(WatchCollector.HandleFor(0), Good(1.5, 0));
        collector.Add(WatchCollector.HandleFor(0), Good(-0.5, 1));
        collector.Add(WatchCollector.HandleFor(0), Good(2.0, 2));
        collector.Add(WatchCollector.HandleFor(1), Good(7, 0));

        var results = collector.Results();
        var sine = results[0];
        Assert.Equal(3, sine.ChangeCount);
        Assert.Equal("1.5", sine.First);
        Assert.Equal("2", sine.Last);
        Assert.Equal(-0.5, sine.Min);
        Assert.Equal(2.0, sine.Max);
        Assert.Equal(1.0, sine.Average);
        Assert.Equal(T0.AddSeconds(1), sine.Changes[1].SourceTimestampUtc);
        Assert.Equal(1, results[1].ChangeCount);
    }

    [Fact]
    public void Keeps_the_latest_changes_and_counts_all()
    {
        var collector = new WatchCollector(["i=1"]);
        for (var i = 0; i < WatchCollector.MaxChangesPerNode + 25; i++)
            collector.Add(WatchCollector.HandleFor(0), Good(i, i));

        var node = Assert.Single(collector.Results());
        Assert.Equal(WatchCollector.MaxChangesPerNode + 25, node.ChangeCount);
        Assert.Equal(WatchCollector.MaxChangesPerNode, node.Changes.Count);
        Assert.True(node.Truncated);
        Assert.Equal("25", node.Changes[0].Value); // the oldest 25 changes were dropped
        Assert.Equal("0", node.First);             // but the summary remembers the first value
    }

    [Fact]
    public void Bad_samples_count_but_do_not_enter_the_statistics()
    {
        var collector = new WatchCollector(["i=1"]);
        collector.Add(WatchCollector.HandleFor(0), Good(10, 0));
        collector.Add(WatchCollector.HandleFor(0),
            new DriverSample(DriverValue.FromFloat64(0), new DriverQuality(DriverQualityLevel.Bad, NativeStatusCode: 0x80310000), T0, T0));

        var node = Assert.Single(collector.Results());
        Assert.Equal(2, node.ChangeCount);
        Assert.Equal(10, node.Max);
        Assert.Equal(10, node.Min);
        Assert.Null(node.Last);
        Assert.Equal("BadNoCommunication", node.Changes[1].StatusName);
    }

    [Fact]
    public void Refused_monitored_item_is_reported_by_name_and_unknown_handles_are_ignored()
    {
        var collector = new WatchCollector(["ns=9;s=Missing"]);
        collector.SetMonitoredItemStatus(0, 0x80340000);
        collector.Add(0, Good(1, 0));
        collector.Add(99, Good(1, 0));

        var node = Assert.Single(collector.Results());
        Assert.Equal("BadNodeIdUnknown", node.Error);
        Assert.Equal(0, node.ChangeCount);
        Assert.Null(node.Average);
    }

    [Fact]
    public void Text_values_have_no_statistics()
    {
        var collector = new WatchCollector(["i=1"]);
        collector.Add(WatchCollector.HandleFor(0), DriverSample.Good(DriverValue.FromString("Running"), T0, T0));

        var node = Assert.Single(collector.Results());
        Assert.Equal("Running", node.Last);
        Assert.Null(node.Min);
        Assert.Null(node.Average);
    }
}
