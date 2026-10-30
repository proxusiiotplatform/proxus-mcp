using Proxus.Drivers;
using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

public class OpcUaStatusCodesTests
{
    [Fact]
    public void Known_code_has_name_description_and_next_step()
    {
        var described = OpcUaStatusCodes.Describe(0x801F0000);

        Assert.NotNull(described);
        Assert.Equal("BadUserAccessDenied", described.Value.Name);
        Assert.False(string.IsNullOrEmpty(described.Value.Description));
        Assert.NotNull(described.Value.NextStep);
    }

    [Fact]
    public void Info_bits_in_the_low_word_are_ignored()
    {
        Assert.Equal("BadNodeIdUnknown", OpcUaStatusCodes.Describe(0x80340000 | 0x0400)?.Name);
    }

    [Fact]
    public void Unknown_and_good_codes_have_no_description()
    {
        Assert.Null(OpcUaStatusCodes.Describe(0x00000000));
        Assert.Null(OpcUaStatusCodes.Describe(0x8FFF0000));
    }

    [Fact]
    public void Explain_adds_the_name_once()
    {
        var error = new DriverError(DriverErrorKind.Device, "UA_SERVICE_FAULT", "ServiceFault.", unchecked((int)0x801F0000), Protocol: "OPC UA Binary/TCP");

        var once = OpcUaStatusCodes.Explain(error);
        var twice = OpcUaStatusCodes.Explain(once);

        Assert.Contains("BadUserAccessDenied", once.Message);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Explain_leaves_other_protocols_alone()
    {
        var error = new DriverError(DriverErrorKind.Device, "MODBUS_EXCEPTION", "Exception 2.", 2, Protocol: "Modbus TCP");

        Assert.Equal(error, OpcUaStatusCodes.Explain(error));
    }
}
