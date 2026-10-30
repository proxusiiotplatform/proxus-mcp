using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

public class RegisterDecoderTests
{
    private static string Value(List<DecodedValue> values, int offset, string type, string order)
    {
        return values.Single(v => v.Offset == offset && v.Type == type && v.ByteOrder == order).Value;
    }

    [Fact]
    public void Word_swapped_float_reads_correctly_only_as_CDAB()
    {
        // 230.5f = 0x43668000; a meter that sends the low word first puts 0x8000 in the first register.
        var values = RegisterDecoder.Decode([0x8000, 0x4366]);

        Assert.Equal("230.5", Value(values, 0, "float32", "CDAB"));
        Assert.NotEqual("230.5", Value(values, 0, "float32", "ABCD"));
    }

    [Fact]
    public void Big_endian_float_reads_as_ABCD()
    {
        var values = RegisterDecoder.Decode([0x4366, 0x8000]);

        Assert.Equal("230.5", Value(values, 0, "float32", "ABCD"));
    }

    [Fact]
    public void Byte_swapped_and_little_endian_orders_are_distinct()
    {
        // 230.5f bytes 43 66 80 00: BADC sends 66 43 00 80, DCBA sends 00 80 66 43.
        Assert.Equal("230.5", Value(RegisterDecoder.Decode([0x6643, 0x0080]), 0, "float32", "BADC"));
        Assert.Equal("230.5", Value(RegisterDecoder.Decode([0x0080, 0x6643]), 0, "float32", "DCBA"));
    }

    [Fact]
    public void Single_register_gives_signed_and_unsigned_16_bit_only()
    {
        var values = RegisterDecoder.Decode([0xFFFB]);

        Assert.Equal("65531", Value(values, 0, "uint16", "AB"));
        Assert.Equal("-5", Value(values, 0, "int16", "AB"));
        Assert.DoesNotContain(values, v => v.Type is "float32" or "int32" or "float64");
    }

    [Fact]
    public void Float64_is_decoded_in_both_word_orders()
    {
        // 1.5 = 0x3FF8000000000000
        Assert.Equal("1.5", Value(RegisterDecoder.Decode([0x3FF8, 0, 0, 0]), 0, "float64", "ABCDEFGH"));
        Assert.Equal("1.5", Value(RegisterDecoder.Decode([0, 0, 0, 0x3FF8]), 0, "float64", "GHEFCDAB"));
    }

    [Fact]
    public void Every_start_offset_is_tried_for_32_bit_values()
    {
        var values = RegisterDecoder.Decode([1, 0x4366, 0x8000]);

        Assert.Equal("230.5", Value(values, 1, "float32", "ABCD"));
    }
}
