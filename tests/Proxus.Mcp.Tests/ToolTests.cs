using System.Text.Json;
using ModelContextProtocol.Protocol;
using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

/// <summary>Tool behaviour that needs no device: argument checks, unknown devices, wrong protocol, a closed port.</summary>
public sealed class ToolTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("proxus-mcp-tools-").FullName;
    private readonly DeviceSessions _sessions;

    public ToolTests()
    {
        var path = Path.Combine(_root, "proxus-mcp.json");
        File.WriteAllText(path, """
            { "devices": {
                "meter": { "protocol": "modbus-tcp", "host": "127.0.0.1", "port": 1, "timeout_ms": 500 },
                "press": { "protocol": "opc-ua", "endpoint": "opc.tcp://127.0.0.1:1", "timeout_ms": 500 } } }
            """);
        var configuration = ServerConfiguration.Load(path);
        _sessions = new DeviceSessions(configuration, new PkiStore(configuration.PkiDir));
    }

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private static (bool IsError, JsonElement Json) Parse(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return (result.IsError == true, JsonDocument.Parse(text).RootElement);
    }

    private static string ErrorCode(JsonElement json)
    {
        return json.GetProperty("error").GetProperty("code").GetString()!;
    }

    [Fact]
    public void List_devices_shows_every_device_without_connecting()
    {
        var (isError, json) = Parse(new DeviceTools(_sessions).ListDevices());

        Assert.False(isError);
        Assert.Equal(2, json.GetArrayLength());
        Assert.All(json.EnumerateArray(), d => Assert.False(d.GetProperty("connected").GetBoolean()));
    }

    [Fact]
    public async Task Unknown_device_is_an_error_that_points_to_list_devices()
    {
        var (isError, json) = Parse(await new ModbusTools(_sessions).ReadAddress("nope", "hr:0"));

        Assert.True(isError);
        Assert.Equal("UNKNOWN_DEVICE", ErrorCode(json));
        Assert.Contains("list_devices", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Tools_refuse_devices_of_another_protocol()
    {
        Assert.Equal("WRONG_PROTOCOL", ErrorCode(Parse(await new ModbusTools(_sessions).ReadAddress("press", "hr:0")).Json));
        Assert.Equal("WRONG_PROTOCOL", ErrorCode(Parse(await new OpcUaTools(_sessions).ReadNodes("meter", ["i=2258"])).Json));
    }

    [Theory]
    [InlineData("zz:1", 1, "MODBUS_ADDRESS")]
    [InlineData("hr:0", 0, "INVALID_ARGUMENT")]
    [InlineData("hr:0", 126, "INVALID_ARGUMENT")]
    [InlineData("coil:0", 2001, "INVALID_ARGUMENT")]
    public async Task Modbus_arguments_are_checked_before_any_network_traffic(string address, int count, string code)
    {
        var (isError, json) = Parse(await new ModbusTools(_sessions).ReadAddress("meter", address, count));

        Assert.True(isError);
        Assert.Equal(code, ErrorCode(json));
    }

    [Fact]
    public async Task Decode_needs_a_register_area_and_at_most_16_registers()
    {
        var tools = new ModbusTools(_sessions);

        Assert.Equal("INVALID_ARGUMENT", ErrorCode(Parse(await tools.DecodeRegisters("meter", "coil:0")).Json));
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(Parse(await tools.DecodeRegisters("meter", "hr:0", 17)).Json));
    }

    [Fact]
    public async Task Read_nodes_checks_its_node_ids()
    {
        var tools = new OpcUaTools(_sessions);

        Assert.Equal("INVALID_ARGUMENT", ErrorCode(Parse(await tools.ReadNodes("press", [])).Json));
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(Parse(await tools.ReadNodes("press", ["bad"])).Json));
        Assert.Equal("INVALID_ARGUMENT", ErrorCode(Parse(await tools.ReadNodes("press", Enumerable.Repeat("i=1", 51).ToArray())).Json));
    }

    [Fact]
    public async Task Closed_port_is_a_transport_error_with_a_next_step()
    {
        var (isError, json) = Parse(await new ModbusTools(_sessions).ReadAddress("meter", "hr:0"));

        Assert.True(isError);
        Assert.Equal("Transport", json.GetProperty("error").GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("error").GetProperty("suggestedAction").GetString()));
    }

    [Fact]
    public async Task Diagnose_names_the_tcp_step_for_a_closed_port()
    {
        var (isError, json) = Parse(await new DiagnoseTools(_sessions, new PkiStore(Path.Combine(_root, "pki"))).DiagnoseConnection("meter"));

        Assert.False(isError); // the report is the answer
        Assert.False(json.GetProperty("healthy").GetBoolean());
        var failed = json.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("status").GetString() == "failed");
        Assert.Equal("tcp", failed.GetProperty("name").GetString());
    }
}
