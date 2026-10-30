using System.Buffers.Binary;
using System.Globalization;

namespace Proxus.Mcp;

/// <summary>
///     Every common reading of a register block side by side. Vendors disagree on byte and word order, and a
///     wrong guess gives a plausible-looking but wrong number, so the caller compares the candidates.
/// </summary>
public static class RegisterDecoder
{
    // Byte order names use A as the most significant byte of the value.
    private static readonly (string Name, int[] Map)[] Orders32 =
    [
        ("ABCD", [0, 1, 2, 3]), // big-endian, Modbus default
        ("CDAB", [2, 3, 0, 1]), // word swap, common on energy meters
        ("BADC", [1, 0, 3, 2]), // byte swap
        ("DCBA", [3, 2, 1, 0])  // little-endian
    ];

    public static List<DecodedValue> Decode(ReadOnlySpan<ushort> registers)
    {
        var result = new List<DecodedValue>();
        Span<byte> wire = stackalloc byte[registers.Length * 2];
        for (var i = 0; i < registers.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(wire[(i * 2)..], registers[i]);

        for (var i = 0; i < registers.Length; i++)
        {
            result.Add(new DecodedValue(i, "uint16", "AB", registers[i].ToString(CultureInfo.InvariantCulture)));
            result.Add(new DecodedValue(i, "int16", "AB", ((short)registers[i]).ToString(CultureInfo.InvariantCulture)));
        }

        Span<byte> value = stackalloc byte[4];
        for (var i = 0; i + 1 < registers.Length; i++)
        {
            var source = wire.Slice(i * 2, 4);
            foreach (var (name, map) in Orders32)
            {
                for (var b = 0; b < 4; b++)
                    value[b] = source[map[b]];

                var float32 = BinaryPrimitives.ReadSingleBigEndian(value);
                result.Add(new DecodedValue(i, "float32", name, Format(float32)));
                result.Add(new DecodedValue(i, "int32", name,
                    BinaryPrimitives.ReadInt32BigEndian(value).ToString(CultureInfo.InvariantCulture)));
                result.Add(new DecodedValue(i, "uint32", name,
                    BinaryPrimitives.ReadUInt32BigEndian(value).ToString(CultureInfo.InvariantCulture)));
            }
        }

        Span<byte> value64 = stackalloc byte[8];
        for (var i = 0; i + 3 < registers.Length; i++)
        {
            var source = wire.Slice(i * 2, 8);
            result.Add(new DecodedValue(i, "float64", "ABCDEFGH", Format(BinaryPrimitives.ReadDoubleBigEndian(source))));
            // Word order reversed: the last register holds the most significant word.
            for (var w = 0; w < 4; w++)
                source.Slice((3 - w) * 2, 2).CopyTo(value64[(w * 2)..]);
            result.Add(new DecodedValue(i, "float64", "GHEFCDAB", Format(BinaryPrimitives.ReadDoubleBigEndian(value64))));
        }

        return result;
    }

    private static string Format(double value)
    {
        return double.IsFinite(value) ? value.ToString("G9", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
    }
}

/// <param name="Offset">Register index within the block where the value starts.</param>
public sealed record DecodedValue(int Offset, string Type, string ByteOrder, string Value);
