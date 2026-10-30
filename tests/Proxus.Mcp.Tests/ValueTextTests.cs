using Proxus.Drivers;
using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

public class ValueTextTests
{
    private static DriverValue Array(params string[] items)
    {
        var values = items.Select(DriverValue.FromString).ToArray();
        var array = DriverDynamicArray.Create(IndustrialDataType.String, [values.Length], values);
        Assert.True(array.IsSuccess);
        return DriverValue.FromArray(array.Value);
    }

    [Fact]
    public void Arrays_show_their_elements()
    {
        Assert.Equal("[http://opcfoundation.org/UA/, urn:plant]", OpcUaTools.ValueText(Array("http://opcfoundation.org/UA/", "urn:plant")));
    }

    [Fact]
    public void Long_arrays_are_cut_with_a_count_of_the_rest()
    {
        var text = OpcUaTools.ValueText(Array(Enumerable.Range(0, 60).Select(static i => i.ToString()).ToArray()));

        Assert.StartsWith("[0, 1, 2", text);
        Assert.EndsWith(", 49, … 10 more]", text);
    }

    [Fact]
    public void Scalars_use_the_invariant_text()
    {
        Assert.Equal("72.5", OpcUaTools.ValueText(DriverValue.FromFloat64(72.5)));
    }
}
