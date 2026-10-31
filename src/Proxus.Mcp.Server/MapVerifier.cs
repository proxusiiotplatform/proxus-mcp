using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Proxus.Mcp;

/// <summary>What reading one map register from the device produced.</summary>
public sealed record RegisterReading(MapRegister Register, ushort[]? Registers, bool? Bit, int? ExceptionCode);

/// <summary>A value a person read off the device's display, to compare with the register.</summary>
public sealed record Confirmation(string Key, string DeviceShows);

/// <summary>
///     Judges a map against readings from a real device and builds the verification report the map collection
///     accepts. Pure: the readings come in, nothing is read here. The levels and the 3% tolerance match
///     tools/verify_report.py in the collection, which recomputes them.
/// </summary>
public static class MapVerifier
{
    private const double ReadableShare = 0.9;
    private const double Tolerance = 0.03;
    private static readonly string[] EnergyUnits = ["Wh", "kWh", "MWh", "varh", "kvarh", "Mvarh", "VAh", "kVAh", "MVAh"];

    public static VerificationReport Evaluate(
        DeviceMap map,
        IReadOnlyList<RegisterReading> readings,
        IReadOnlyList<(string Key, string DeviceShows, double? Read)> confirmations,
        IReadOnlyDictionary<string, (double First, double Second)> energyRereads,
        string tool,
        DateOnly date)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        var notAvailable = 0;
        var read = 0;
        var byKey = map.Registers.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var reading in readings)
        {
            var register = reading.Register;
            if (reading.ExceptionCode is not null)
            {
                unreadable.Add(register.Key);
                continue;
            }

            read++;
            if (reading.Bit is not null || reading.Registers is null)
                continue;
            if (IsNotAvailable(register, reading.Registers))
            {
                notAvailable++;
                continue;
            }

            if (register.Type == "string")
            {
                texts[register.Key] = MapValueDecoder.Text(reading.Registers, register.ByteOrder ?? "ABCD");
                continue;
            }

            var number = MapValueDecoder.Number(register, reading.Registers, register.ByteOrder ?? map.DefaultByteOrder);
            if (number is null)
                continue;
            int? exponent = null;
            if (register.ScaleFactor is { } sfKey && byKey.ContainsKey(sfKey))
                exponent = readings.FirstOrDefault(r => r.Register.Key.Equals(sfKey, StringComparison.OrdinalIgnoreCase))?.Registers is [var sf, ..]
                    ? (short)sf
                    : null;
            values[register.Key] = MapValueDecoder.Scaled(number.Value, register.Scale, exponent);
        }

        var checks = new List<VerificationCheck>();
        AddFloatCheck(map, readings, checks);
        AddRangeCheck(map, values, checks, "frequency_range", r => r.Unit == "Hz" && Contains(r.Name, "freq") && r.Access == "r",
            v => v is >= 45 and <= 65, skip: v => v == 0, describe: v => $"{Format(v)} Hz");
        AddRangeCheck(map, values, checks, "voltage_range", r => r.Unit is "V" or "kV" && Contains(r.Name, "volt") && r.Access == "r",
            v => v is >= 0 and <= 1_000_000, skip: _ => false, describe: v => $"{Format(v)} V", allOf: true);
        AddRangeCheck(map, values, checks, "power_factor_range",
            r => r.Unit is null && Contains(r.Name, "power factor") && r.Access == "r" && !(r.Notes?.Contains("4Q", StringComparison.Ordinal) ?? false),
            v => v is >= -1.05 and <= 1.05, skip: _ => false, describe: Format, allOf: true);
        if (energyRereads.Count > 0)
        {
            var backwards = energyRereads.Where(e => e.Value.Second < e.Value.First).Select(e => e.Key).ToList();
            checks.Add(new VerificationCheck("energy_not_decreasing", backwards.FirstOrDefault() ?? energyRereads.Keys.First(),
                backwards.Count == 0, backwards.Count == 0 ? $"{energyRereads.Count} counters" : $"{backwards.Count} counters ran backwards"));
        }

        var model = FindText(texts, "model", "type_designation");
        if (model is not null && model.Length > 0)
        {
            var names = map.Aliases.Append(map.Model).ToList();
            var matches = names.Any(n => model.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(model, StringComparison.OrdinalIgnoreCase))
                          || map.Model.Split('/', ' ').Any(part => part.Length >= 3 && model.Contains(part.TrimEnd('x', 'X'), StringComparison.OrdinalIgnoreCase));
            checks.Add(new VerificationCheck("model_matches_map", KeyOf(texts, model), matches, Clean(model)));
        }

        var confirmed = confirmations
            .Where(c => c.Read is { } r && double.IsFinite(r))
            .Select(c => new VerificationConfirmation(c.Key, c.DeviceShows, MapValueDecoder.Format(c.Read!.Value), byKey[c.Key].Unit is { } u ? Clean(u) : null))
            .ToList();

        var checkedCount = readings.Count;
        var level = Level(checkedCount, read, checks, confirmed);
        return new VerificationReport(
            1,
            tool,
            map.Id,
            map.ContentSha256 ?? string.Empty,
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            new VerificationDevice(
                model is { Length: > 0 } ? Clean(model) : null,
                FirmwareOf(texts, values) is { } firmware ? Clean(firmware) : null,
                SerialHash(map, readings)),
            level,
            new VerificationRegisters(checkedCount, read, notAvailable, unreadable.Count, unreadable.Take(50).ToList()),
            checks,
            confirmed.Count > 0 ? confirmed : null);
    }

    /// <summary>The report as the collection's verification issue expects it.</summary>
    public static string ToJson(VerificationReport report)
    {
        return System.Text.Json.JsonSerializer.Serialize(report, ReportJsonContext.Default.VerificationReport);
    }

    public static bool WithinTolerance(string deviceShows, double read)
    {
        var text = deviceShows.Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var shown))
            return false;
        var decimals = text.Contains('.') ? text.Length - text.IndexOf('.') - 1 : 0;
        return Math.Abs(read - shown) <= Math.Max(Tolerance * Math.Abs(shown), 0.5 * Math.Pow(10, -decimals));
    }

    public static string Level(int checkedCount, int read, List<VerificationCheck> checks, List<VerificationConfirmation> confirmed)
    {
        if (checkedCount == 0 || (double)read / checkedCount < ReadableShare)
            return "L0";
        if (checks.Count == 0 || checks.Any(c => !c.Passed))
            return "L1";
        var matching = confirmed.Count(c => double.TryParse(c.Read, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && WithinTolerance(c.DeviceShows, r));
        return matching >= 2 ? "L3" : "L2";
    }

    /// <summary>Floats from a wrong word order or address look like 1e-38 or 1e+30; a few odd ones are normal.</summary>
    private static void AddFloatCheck(DeviceMap map, IReadOnlyList<RegisterReading> readings, List<VerificationCheck> checks)
    {
        var floats = readings
            .Where(r => r.Registers is not null && r.Register.Type is "float32" or "float64" && !IsNotAvailable(r.Register, r.Registers))
            .Select(r => (r.Register.Key, Value: MapValueDecoder.Number(r.Register, r.Registers!, r.Register.ByteOrder ?? map.DefaultByteOrder) ?? 0))
            .ToList();
        if (floats.Count == 0)
            return;
        // Real readings are zero or well above 1e-9; a wrong word order mostly lands far outside.
        var odd = floats.Where(f => !double.IsFinite(f.Value) || (f.Value != 0 && Math.Abs(f.Value) < 1e-9) || Math.Abs(f.Value) > 1e15).ToList();
        var passed = odd.Count <= floats.Count / 50;
        checks.Add(new VerificationCheck("plausible_floats", odd.FirstOrDefault().Key, passed,
            $"{odd.Count} of {floats.Count} floats implausible"));
    }

    private static void AddRangeCheck(
        DeviceMap map,
        Dictionary<string, double> values,
        List<VerificationCheck> checks,
        string name,
        Func<MapRegister, bool> applies,
        Func<double, bool> inRange,
        Func<double, bool> skip,
        Func<double, string> describe,
        bool allOf = false)
    {
        var candidates = map.Registers.Where(r => applies(r) && values.ContainsKey(r.Key) && !skip(values[r.Key])).ToList();
        if (candidates.Count == 0)
            return;
        if (!allOf)
        {
            var first = candidates[0];
            checks.Add(new VerificationCheck(name, first.Key, inRange(values[first.Key]), describe(values[first.Key])));
            return;
        }

        var outside = candidates.FirstOrDefault(r => !inRange(values[r.Key]));
        var shown = outside ?? candidates[0];
        checks.Add(new VerificationCheck(name, shown.Key, outside is null,
            outside is null ? $"{candidates.Count} values in range" : $"{describe(values[outside.Key])} out of range"));
    }

    /// <summary>The vendor's "not available" patterns: all ones (unsigned), 0x8000... (signed) or a NaN float.</summary>
    public static bool IsNotAvailable(MapRegister register, ushort[] registers)
    {
        if (register.Type is "string" or "raw" or "bitfield" or "bitfield16")
            return false;
        if (registers.All(r => r == 0xFFFF))
            return true;
        if (register.Type is "int16" or "int32" or "int64" && registers[0] == 0x8000 && registers.Skip(1).All(r => r == 0))
            return true;
        return register.Type is "float32" or "float64" &&
               MapValueDecoder.Number(register, registers, "ABCD") is { } v && double.IsNaN(v);
    }

    public static bool IsEnergyCounter(MapRegister register)
    {
        return register.Access == "r" && register.Unit is { } unit && EnergyUnits.Contains(unit) &&
               register.Type is not ("string" or "raw" or "bitfield" or "bitfield16") &&
               !Contains(register.Name, "net") && !Contains(register.Name, "demand");
    }

    private static string? FindText(Dictionary<string, string> texts, params string[] keyParts)
    {
        return texts.FirstOrDefault(t => keyParts.Any(p => t.Key.Contains(p, StringComparison.OrdinalIgnoreCase))).Value;
    }

    private static string? KeyOf(Dictionary<string, string> texts, string value)
    {
        return texts.FirstOrDefault(t => t.Value == value).Key;
    }

    private static string? FirmwareOf(Dictionary<string, string> texts, Dictionary<string, double> values)
    {
        var text = FindText(texts, "firmware", "software_version", "fw_version");
        if (text is { Length: > 0 })
            return text;
        var number = values.FirstOrDefault(v => v.Key.Contains("firmware", StringComparison.OrdinalIgnoreCase) ||
                                                v.Key.Contains("software_version", StringComparison.OrdinalIgnoreCase));
        // 0 is what an unset or unmapped register reads; it says nothing about the firmware.
        return number.Key is null || number.Value == 0 ? null : Format(number.Value);
    }

    /// <summary>A serial number identifies one device; the report keeps only a salted hash, enough to tell devices apart.</summary>
    private static string? SerialHash(DeviceMap map, IReadOnlyList<RegisterReading> readings)
    {
        var serial = readings.FirstOrDefault(r => r.Register.Key.Contains("serial", StringComparison.OrdinalIgnoreCase) && r.Registers is not null);
        if (serial?.Registers is null)
            return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(map.Id + ":" + MapValueDecoder.Hex(serial.Registers)));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static string Format(double value) => MapValueDecoder.Format(value);

    /// <summary>Keeps report text to the characters the collection's schema accepts.</summary>
    public static string Clean(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || " ._/()+%°:-".Contains(c))
                builder.Append(c);
            if (builder.Length == 64)
                break;
        }

        return builder.ToString().Trim();
    }
}

/// <summary>The report in the collection's verification-report schema (snake_case JSON).</summary>
public sealed record VerificationReport(
    int ReportVersion,
    string Tool,
    string Map,
    string MapSha256,
    string Date,
    VerificationDevice Device,
    string Level,
    VerificationRegisters Registers,
    List<VerificationCheck> Checks,
    List<VerificationConfirmation>? Confirmed);

public sealed record VerificationDevice(string? Model, string? Firmware, string? SerialHash);

public sealed record VerificationRegisters(int Checked, int Read, int NotAvailable, int UnreadableCount, List<string> Unreadable);

public sealed record VerificationCheck(string Name, string? Key, bool Passed, string? Detail)
{
    public string? Detail { get; init; } = Detail is null ? null : MapVerifier.Clean(Detail);
}

public sealed record VerificationConfirmation(string Key, string DeviceShows, string Read, string? Unit);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VerificationReport))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;
