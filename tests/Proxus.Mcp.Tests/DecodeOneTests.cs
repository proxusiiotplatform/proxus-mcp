using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

public class DecodeOneTests
{
    [Theory]
    [InlineData(new ushort[] { 0x8000, 0x4366 }, "float32", "CDAB", "230.5")]
    [InlineData(new ushort[] { 0x4366, 0x8000 }, "float32", "ABCD", "230.5")]
    [InlineData(new ushort[] { 0x3F8C, 0xCCCD }, "float32", "ABCD", "1.1")]
    [InlineData(new ushort[] { 0xFFFB }, "int16", "ABCD", "-5")]
    [InlineData(new ushort[] { 0xFFFB }, "uint16", "ABCD", "65531")]
    [InlineData(new ushort[] { 0x0001, 0x0000 }, "uint32", "CDAB", "1")]
    [InlineData(new ushort[] { 0xFFFF, 0xFFFE }, "int32", "ABCD", "-2")]
    public void Decodes_one_value_in_the_requested_type_and_order(ushort[] registers, string type, string order, string expected)
    {
        Assert.Equal(expected, RegisterDecoder.DecodeOne(registers, 0, type, order).ToInvariantString());
    }

    [Fact]
    public void Value_types_know_their_width()
    {
        Assert.Equal(1, RegisterDecoder.RegistersPerValue("int16"));
        Assert.Equal(2, RegisterDecoder.RegistersPerValue("float32"));
        Assert.True(RegisterDecoder.IsByteOrder("CDAB"));
        Assert.False(RegisterDecoder.IsByteOrder("XYZW"));
    }
}
