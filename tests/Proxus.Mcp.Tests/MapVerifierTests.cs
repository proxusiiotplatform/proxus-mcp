using System.Buffers.Binary;
using System.Text.Json;
using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

/// <summary>Judging a map from device readings: levels, checks and the report the collection accepts.</summary>
public sealed class MapVerifierTests
{
    private static readonly DeviceMap Meter = new()
    {
        Id = "acme/meter",
        Model = "M-100",
        Aliases = ["M100"],
        ContentSha256 = new string('a', 64),
        Connection = new MapConnection { DefaultByteOrder = "ABCD" },
        Registers =
        [
            new MapRegister { Key = "voltage_l1_n", Name = "Voltage L1-N", Address = 0, Type = "float32", Unit = "V" },
            new MapRegister { Key = "frequency", Name = "Frequency", Address = 2, Type = "float32", Unit = "Hz" },
            new MapRegister { Key = "power_factor", Name = "Power factor", Address = 4, Type = "int16", Scale = 0.001 },
            new MapRegister { Key = "import_energy", Name = "Active energy import", Address = 5, Type = "uint32", Unit = "kWh", Scale = 0.01 },
            new MapRegister { Key = "model_name", Name = "Model", Address = 7, Type = "string", Length = 4 },
            new MapRegister { Key = "serial_number", Name = "Serial number", Address = 11, Type = "uint32" }
        ]
    };

    private static ushort[] Float(float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, value);
        return [BinaryPrimitives.ReadUInt16BigEndian(bytes), BinaryPrimitives.ReadUInt16BigEndian(bytes[2..])];
    }

    private static List<RegisterReading> Healthy(bool swapWords = false)
    {
        ushort[] Order(ushort[] r) => swapWords ? [r[1], r[0]] : r;
        var r = Meter.Registers;
        return
        [
            new(r[0], Order(Float(231.2f)), null, null),
            new(r[1], Order(Float(50.01f)), null, null),
            new(r[2], [985], null, null),
            new(r[3], [0x0001, 0x86A0], null, null),
            new(r[4], [0x4D31, 0x3030, 0, 0], null, null), // "M100"
            new(r[5], [0x0012, 0xD687], null, null)
        ];
    }

    private static VerificationReport Evaluate(List<RegisterReading> readings, params (string, string, double?)[] confirmations)
    {
        return MapVerifier.Evaluate(Meter, readings, confirmations, new Dictionary<string, (double, double)> { ["import_energy"] = (1000, 1000.5) },
            "proxus-mcp 0.0.0-test", new DateOnly(2026, 10, 31));
    }

    [Fact]
    public void A_healthy_device_reaches_L2_and_two_display_matches_reach_L3()
    {
        var automatic = Evaluate(Healthy());
        Assert.Equal("L2", automatic.Level);
        Assert.All(automatic.Checks, c => Assert.True(c.Passed, c.Name));
        Assert.Contains(automatic.Checks, c => c.Name == "frequency_range");
        Assert.Contains(automatic.Checks, c => c.Name == "model_matches_map");
        Assert.Equal("M100", automatic.Device.Model);

        var confirmed = Evaluate(Healthy(), ("voltage_l1_n", "231.0", 231.2), ("frequency", "50.0", 50.01));
        Assert.Equal("L3", confirmed.Level);
    }

    [Fact]
    public void A_display_mismatch_keeps_the_level_at_L2()
    {
        Assert.Equal("L2", Evaluate(Healthy(), ("voltage_l1_n", "240", 231.2), ("frequency", "50.0", 50.01)).Level);
    }

    [Fact]
    public void A_wrong_word_order_fails_the_float_check()
    {
        var report = Evaluate(Healthy(swapWords: true));

        Assert.Equal("L1", report.Level);
        Assert.Contains(report.Checks, c => c.Name == "plausible_floats" && !c.Passed);
    }

    [Fact]
    public void Refused_registers_are_listed_and_too_many_mean_L0()
    {
        var readings = Healthy();
        readings[1] = new RegisterReading(Meter.Registers[1], null, null, 2);
        var one = Evaluate(readings);
        Assert.Equal(["frequency"], one.Registers.Unreadable);
        Assert.Equal(5, one.Registers.Read);

        var many = Healthy().Select(r => r with { Registers = null, ExceptionCode = 2 }).ToList();
        Assert.Equal("L0", Evaluate(many).Level);
    }

    [Fact]
    public void Not_available_patterns_do_not_count_as_failures()
    {
        var readings = Healthy();
        readings[0] = new RegisterReading(Meter.Registers[0], [0xFFC0, 0x0000], null, null); // NaN: not available

        var report = Evaluate(readings);

        Assert.Equal(1, report.Registers.NotAvailable);
        Assert.Equal("L2", report.Level);
    }

    [Fact]
    public void A_counter_running_backwards_fails()
    {
        var report = MapVerifier.Evaluate(Meter, Healthy(), [], new Dictionary<string, (double, double)> { ["import_energy"] = (1000, 999) },
            "proxus-mcp test", new DateOnly(2026, 10, 31));

        Assert.Contains(report.Checks, c => c.Name == "energy_not_decreasing" && !c.Passed);
    }

    [Fact]
    public void The_report_has_no_serial_number_only_a_short_hash()
    {
        var json = MapVerifier.ToJson(Evaluate(Healthy()));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("report_version").GetInt32());
        Assert.Equal(16, root.GetProperty("device").GetProperty("serial_hash").GetString()!.Length);
        Assert.DoesNotContain("1234567", json); // 0x0012D687
        Assert.Equal(new string('a', 64), root.GetProperty("map_sha256").GetString());
    }

    [Theory]
    [InlineData("231.2", 231.18, true)]
    [InlineData("50,0", 50.01, true)]
    [InlineData("230", 240, false)]
    [InlineData("0", 0.4, true)]
    [InlineData("0.0", 0.4, false)]
    public void Display_tolerance_matches_the_collection(string shown, double read, bool expected)
    {
        Assert.Equal(expected, MapVerifier.WithinTolerance(shown, read));
    }

    [Fact]
    public void The_submit_link_prefills_the_verification_issue()
    {
        var url = VerifyTools.SubmitUrl("acme/meter", """{"report_version":1}""")!;

        Assert.StartsWith("https://github.com/proxusiiotplatform/modbus-register-maps/issues/new?template=verification.yml", url);
        Assert.Contains("report=%7B%22report_version%22%3A1%7D", url);
        Assert.Null(VerifyTools.SubmitUrl("acme/meter", new string('x', 8000)));
    }
}
