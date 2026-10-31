using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Proxus.Mcp;

/// <summary>Turns the registers of one map entry into a value, using the entry's type, byte order and scale.</summary>
public static class MapValueDecoder
{
    /// <summary>
    ///     Puts the registers' bytes in big-endian value order. Byte order names use A as the most significant byte
    ///     and extend to any width: ABCD keeps the wire order, CDAB reverses the 16-bit words, BADC swaps the bytes
    ///     inside each word and DCBA reverses every byte.
    /// </summary>
    public static byte[] ValueBytes(ReadOnlySpan<ushort> registers, string byteOrder)
    {
        var words = registers.ToArray();
        if (byteOrder is "CDAB" or "DCBA")
            Array.Reverse(words);
        var bytes = new byte[words.Length * 2];
        for (var i = 0; i < words.Length; i++)
        {
            var word = byteOrder is "BADC" or "DCBA" ? BinaryPrimitives.ReverseEndianness(words[i]) : words[i];
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), word);
        }

        return bytes;
    }

    /// <summary>The raw number before scaling, or null for strings, bit fields and vendor encodings.</summary>
    public static double? Number(MapRegister register, ReadOnlySpan<ushort> registers, string byteOrder)
    {
        if (register.Type is "uint16" or "bitfield16")
            return registers[0];
        if (register.Type == "int16")
            return (short)registers[0];

        var bytes = ValueBytes(registers[..register.Count], byteOrder);
        return register.Type switch
        {
            "uint32" => BinaryPrimitives.ReadUInt32BigEndian(bytes),
            "int32" => BinaryPrimitives.ReadInt32BigEndian(bytes),
            // Through the float's shortest text so 400.2f stays 400.2 instead of 400.200012207.
            "float32" => double.Parse(BinaryPrimitives.ReadSingleBigEndian(bytes).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            "uint64" => BinaryPrimitives.ReadUInt64BigEndian(bytes),
            "int64" => BinaryPrimitives.ReadInt64BigEndian(bytes),
            "float64" => BinaryPrimitives.ReadDoubleBigEndian(bytes),
            _ => null
        };
    }

    /// <summary>ASCII text of a string register block, without trailing NULs and spaces.</summary>
    public static string Text(ReadOnlySpan<ushort> registers, string byteOrder)
    {
        var bytes = ValueBytes(registers, byteOrder is "BADC" ? "BADC" : "ABCD");
        var text = Encoding.ASCII.GetString(bytes);
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).TrimEnd();
    }

    public static string Hex(ReadOnlySpan<ushort> registers)
    {
        var builder = new StringBuilder("0x");
        foreach (var register in registers)
            builder.Append(register.ToString("X4", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <summary>Applies scale or a SunSpec scale factor (power of ten) to a raw number.</summary>
    public static double Scaled(double raw, double? scale, int? scaleFactorExponent)
    {
        if (scaleFactorExponent is { } exponent)
            return raw * Math.Pow(10, exponent);
        return scale is { } factor ? raw * factor : raw;
    }

    public static string Format(double value)
    {
        if (!double.IsFinite(value))
            return value.ToString(CultureInfo.InvariantCulture);
        // Round away binary noise from scaling (230.10000000000002 -> 230.1).
        return Math.Round(value, 10).ToString("G12", CultureInfo.InvariantCulture);
    }
}
