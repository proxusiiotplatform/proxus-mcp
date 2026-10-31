using System.Text.Json;
using ModelContextProtocol.Protocol;
using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

/// <summary>Register maps: loading, resolving device maps, decoding values and the map tools that need no device.</summary>
public sealed class DeviceMapTests : IDisposable
{
    private const string MeterMap = """
        {
          "id": "acme/meter",
          "vendor": "Acme",
          "model": "M-100",
          "aliases": ["Acme M100"],
          "category": "energy-meter",
          "summary": "Test meter.",
          "connection": { "interfaces": ["tcp"], "default_byte_order": "CDAB" },
          "sources": [{ "title": "Manual", "publisher": "Acme", "url": "https://example.com/m100.pdf" }],
          "registers": [
            { "key": "voltage", "name": "Voltage L1-N", "group": "voltage", "table": "input", "address": 0, "type": "float32", "unit": "V", "access": "r" },
            { "key": "energy", "name": "Active energy import", "group": "energy", "table": "holding", "address": 100, "type": "uint64", "scale": 0.01, "unit": "kWh", "access": "r" },
            { "key": "reset", "name": "Reset", "table": "holding", "address": 200, "type": "uint16", "access": "w" }
          ]
        }
        """;

    /// <summary>Release builds embed the map collection; then a server is never without maps.</summary>
    private static bool HasBuiltInMaps => DeviceMapLibrary.Load(null).Count > 0;

    private readonly string _directory = Directory.CreateTempSubdirectory("proxus-mcp-maps-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    private ServerConfiguration Load(string json)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "maps", "acme"));
        File.WriteAllText(Path.Combine(_directory, "maps", "acme", "meter.json"), MeterMap);
        File.WriteAllText(Path.Combine(_directory, "maps", "index.json"), """{ "schema_version": 1, "devices": [] }""");
        var path = Path.Combine(_directory, "proxus-mcp.json");
        File.WriteAllText(path, json);
        return ServerConfiguration.Load(path);
    }

    private static JsonElement Parse(CallToolResult result, out bool isError)
    {
        isError = result.IsError == true;
        return JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement;
    }

    [Fact]
    public void A_device_map_is_resolved_by_id_from_maps_dir()
    {
        var configuration = Load("""
            { "maps_dir": "maps", "devices": { "m": { "host": "10.0.0.1", "map": "ACME/meter" } } }
            """);

        Assert.NotNull(configuration.Maps.Find("acme/meter"));
        Assert.Equal("acme/meter", configuration.Devices["m"].ResolvedMap!.Id);
    }

    [Fact]
    public void A_device_map_can_be_a_json_file_next_to_the_configuration()
    {
        var configuration = Load("""{ "devices": { "m": { "host": "10.0.0.1", "map": "maps/acme/meter.json" } } }""");

        Assert.Equal("acme/meter", configuration.Devices["m"].ResolvedMap!.Id);
    }

    [Fact]
    public void An_unknown_map_stops_the_server_with_a_hint()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            Load("""{ "maps_dir": "maps", "devices": { "m": { "host": "10.0.0.1", "map": "acme/other" } } }"""));
        Assert.Contains("find_device_map", error.Message);

        if (HasBuiltInMaps)
            return;
        var noLibrary = Assert.Throws<InvalidDataException>(() =>
            Load("""{ "devices": { "m": { "host": "10.0.0.1", "map": "acme/meter" } } }"""));
        Assert.Contains("maps_dir", noLibrary.Message);
    }

    [Fact]
    public void Opc_ua_devices_cannot_have_a_register_map()
    {
        Assert.Throws<InvalidDataException>(() => Load("""
            { "maps_dir": "maps", "devices": { "p": { "protocol": "opc-ua", "endpoint": "opc.tcp://h:4840", "map": "acme/meter" } } }
            """));
    }

    [Theory]
    [InlineData("ABCD", new ushort[] { 0x4366, 0x8000 })]
    [InlineData("CDAB", new ushort[] { 0x8000, 0x4366 })]
    [InlineData("BADC", new ushort[] { 0x6643, 0x0080 })]
    [InlineData("DCBA", new ushort[] { 0x0080, 0x6643 })]
    public void Float32_is_decoded_in_every_byte_order(string order, ushort[] registers)
    {
        var register = new MapRegister { Type = "float32" };

        Assert.Equal(230.5, MapValueDecoder.Number(register, registers, order));
    }

    [Fact]
    public void Float32_values_keep_their_shortest_form()
    {
        var register = new MapRegister { Type = "float32" };
        ushort[] registers = [0x43C8, 0x199A]; // 400.2f

        Assert.Equal("400.2", MapValueDecoder.Format(MapValueDecoder.Number(register, registers, "ABCD")!.Value));
    }

    [Fact]
    public void Sixty_four_bit_words_follow_the_same_byte_order_names()
    {
        var register = new MapRegister { Type = "uint64" };
        ushort[] bigEndian = [0x0000, 0x0000, 0x0001, 0x86A0]; // 100000

        Assert.Equal(100000, MapValueDecoder.Number(register, bigEndian, "ABCD"));
        Assert.Equal(100000, MapValueDecoder.Number(register, bigEndian.Reverse().ToArray(), "CDAB"));
    }

    [Fact]
    public void Scale_and_scale_factor_give_engineering_values()
    {
        Assert.Equal("230.1", MapValueDecoder.Format(MapValueDecoder.Scaled(2301, 0.1, null)));
        Assert.Equal("12.34", MapValueDecoder.Format(MapValueDecoder.Scaled(1234, null, -2)));
        Assert.Equal("1234", MapValueDecoder.Format(MapValueDecoder.Scaled(1234, null, null)));
    }

    [Fact]
    public void Strings_stop_at_the_first_nul()
    {
        ushort[] registers = [0x504D, 0x3535, 0x3630, 0x0000, 0x2020]; // "PM5560"

        Assert.Equal("PM5560", MapValueDecoder.Text(registers, "ABCD"));
    }

    [Fact]
    public void Find_device_map_matches_vendor_model_and_aliases()
    {
        var configuration = Load("""{ "maps_dir": "maps", "devices": {} }""");
        var tools = new MapTools(new DeviceSessions(configuration, new PkiStore(configuration.PkiDir)));

        var json = Parse(tools.FindDeviceMap("acme m100"), out var isError);

        Assert.False(isError);
        Assert.Equal("acme/meter", json.GetProperty("maps")[0].GetProperty("id").GetString());
        Assert.Equal(0, Parse(tools.FindDeviceMap("no-such-vendor"), out _).GetProperty("matches").GetInt32());
    }

    [Fact]
    public void Without_maps_the_tools_say_how_to_add_them()
    {
        if (HasBuiltInMaps)
            return;
        var configuration = Load("""{ "devices": {} }""");
        var tools = new MapTools(new DeviceSessions(configuration, new PkiStore(configuration.PkiDir)));

        var json = Parse(tools.FindDeviceMap("anything"), out var isError);

        Assert.True(isError);
        Assert.Equal("NO_MAPS", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Get_device_map_filters_and_shows_read_address_notation()
    {
        var configuration = Load("""{ "maps_dir": "maps", "devices": { "m": { "host": "10.0.0.1", "map": "acme/meter" } } }""");
        var tools = new MapTools(new DeviceSessions(configuration, new PkiStore(configuration.PkiDir)));

        var json = Parse(tools.GetDeviceMap("m", "voltage"), out var isError);

        Assert.False(isError);
        Assert.Equal(1, json.GetProperty("matching").GetInt32());
        var register = json.GetProperty("registers")[0];
        Assert.Equal("ir:0", register.GetProperty("address").GetString());
        Assert.Equal("CDAB", register.GetProperty("byteOrder").GetString());
    }

    [Fact]
    public async Task Read_named_checks_keys_and_maps_before_connecting()
    {
        var configuration = Load("""
            { "maps_dir": "maps", "devices": {
                "m": { "host": "127.0.0.1", "port": 1, "map": "acme/meter", "timeout_ms": 500 },
                "bare": { "host": "127.0.0.1", "port": 1, "timeout_ms": 500 } } }
            """);
        await using var sessions = new DeviceSessions(configuration, new PkiStore(configuration.PkiDir));
        var tools = new MapTools(sessions);

        var unknownKey = Parse(await tools.ReadNamed("m", ["nope"]), out var e1);
        var noMap = Parse(await tools.ReadNamed("bare", ["voltage"]), out var e2);
        var unknownDevice = Parse(await tools.ReadNamed("ghost", ["voltage"]), out var e3);

        Assert.True(e1 && e2 && e3);
        Assert.Contains("Unknown keys: nope", unknownKey.GetProperty("error").GetProperty("message").GetString());
        Assert.Contains("no register map", noMap.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("UNKNOWN_DEVICE", unknownDevice.GetProperty("error").GetProperty("code").GetString());
    }
}
