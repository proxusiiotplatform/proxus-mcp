using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Proxus.Drivers;
using Proxus.Drivers.Modbus;

namespace Proxus.Mcp;

/// <summary>Device inventory, shared by every protocol.</summary>
[McpServerToolType]
public sealed class DeviceTools(DeviceSessions sessions)
{
    [McpServerTool(Name = "list_devices", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the industrial devices this server is configured to reach, with protocol, endpoint, ISA-95 path and whether a connection is open. Call this first; other tools take a device name from this list. Modbus devices use read_address and decode_registers; OPC UA devices use browse and read_nodes.")]
    public CallToolResult ListDevices()
    {
        var devices = sessions.Configuration.Devices
            .Select(pair => new DeviceSummary(
                pair.Key,
                pair.Value.Protocol,
                pair.Value.DisplayEndpoint,
                pair.Value.Path,
                sessions.IsConnected(pair.Key)))
            .ToList();
        return ToolOutput.Ok(devices, ToolJsonContext.Readable.ListDeviceSummary);
    }
}

/// <summary>Read-only Modbus tools. Nothing here can write to a device.</summary>
[McpServerToolType]
public sealed class ModbusTools(DeviceSessions sessions)
{
    private const int MaxRegisters = 125; // largest block a single Modbus read (FC03/FC04) returns
    private const int MaxBits = 2000;
    private const int MaxDecodeRegisters = 16;
    private const int MaxWatchedValues = 16;

    [McpServerTool(Name = "read_address", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads raw Modbus data from a configured device. Address: hr:0 (holding register), ir:0 (input register), coil:0, di:0 with zero-based offsets, or reference notation such as 40001 (holding register 0). Returns raw 16-bit registers or bits. Use decode_registers when the registers hold 32-bit or floating-point values.")]
    public async Task<CallToolResult> ReadAddress(
        [Description("Device name from list_devices.")] string device,
        [Description("Start address, e.g. hr:0, ir:10, coil:5, di:0 or 40001.")] string address,
        [Description("Number of registers (1-125) or bits (1-2000) to read.")] int count = 1,
        CancellationToken cancellationToken = default)
    {
        var parsed = ModbusAddress.Parse(address);
        if (!parsed.IsSuccess)
            return Fail(ReadResult.Failed(device, address, parsed.Error));

        var isBit = parsed.Value.ObjectType is ModbusObjectType.Coil or ModbusObjectType.DiscreteInput;
        var limit = isBit ? MaxBits : MaxRegisters;
        if (count < 1 || count > limit)
            return Fail(ReadResult.Failed(device, address, Invalid($"count must be between 1 and {limit} for this area.")));

        var session = await sessions.GetModbusAsync(device, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
            return Fail(ReadResult.Failed(device, address, session.Error));

        var client = session.Value;
        var start = parsed.Value.Offset;
        if (isBit)
        {
            var bits = new bool[count];
            var read = parsed.Value.ObjectType == ModbusObjectType.Coil
                ? await client.ReadCoilsAsync(start, bits, parsed.Value.UnitId, cancellationToken).ConfigureAwait(false)
                : await client.ReadDiscreteInputsAsync(start, bits, parsed.Value.UnitId, cancellationToken).ConfigureAwait(false);
            return read.IsSuccess
                ? ToolOutput.Ok(new ReadResult(device, parsed.Value.ToString(), DateTimeOffset.UtcNow, null, bits, null), Json.ReadResult)
                : Fail(ReadResult.Failed(device, address, read.Error));
        }

        var registers = new ushort[count];
        var readRegisters = await ReadRegistersAsync(client, parsed.Value, registers, cancellationToken).ConfigureAwait(false);
        return readRegisters.IsSuccess
            ? ToolOutput.Ok(new ReadResult(device, parsed.Value.ToString(), DateTimeOffset.UtcNow, registers, null, null), Json.ReadResult)
            : Fail(ReadResult.Failed(device, address, readRegisters.Error));
    }

    [McpServerTool(Name = "decode_registers", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads up to 16 holding or input registers and shows every common interpretation side by side: uint16/int16, int32/uint32/float32 in byte orders ABCD, CDAB, BADC and DCBA, and float64 in both word orders. Use it when a value looks wrong (a meter showing 1.2e-38 instead of 230 V usually means the wrong word order): pick the interpretation that is physically plausible.")]
    public async Task<CallToolResult> DecodeRegisters(
        [Description("Device name from list_devices.")] string device,
        [Description("Start register, e.g. hr:0, ir:12 or 30013.")] string address,
        [Description("Number of registers to read (1-16). Use 2 for one 32-bit value, 4 for one 64-bit value.")] int count = 2,
        CancellationToken cancellationToken = default)
    {
        var parsed = ModbusAddress.Parse(address);
        if (!parsed.IsSuccess)
            return Fail(DecodeResult.Failed(device, address, parsed.Error));
        if (parsed.Value.ObjectType is not (ModbusObjectType.HoldingRegister or ModbusObjectType.InputRegister))
            return Fail(DecodeResult.Failed(device, address, Invalid("decode_registers needs a holding (hr:) or input (ir:) register address.")));
        if (count < 1 || count > MaxDecodeRegisters)
            return Fail(DecodeResult.Failed(device, address, Invalid($"count must be between 1 and {MaxDecodeRegisters}.")));

        var session = await sessions.GetModbusAsync(device, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
            return Fail(DecodeResult.Failed(device, address, session.Error));

        var registers = new ushort[count];
        var read = await ReadRegistersAsync(session.Value, parsed.Value, registers, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Fail(DecodeResult.Failed(device, address, read.Error));

        return ToolOutput.Ok(new DecodeResult(
            device,
            parsed.Value.ToString(),
            DateTimeOffset.UtcNow,
            registers,
            registers.Select(static r => $"0x{r:X4}").ToArray(),
            RegisterDecoder.Decode(registers),
            null), Json.DecodeResult);
    }

    [McpServerTool(Name = "watch_address", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Polls a Modbus address for a few seconds and returns how the values moved: per value the change count, first and last value, min, max and average, and the most recent changes with timestamps. Registers can be decoded as uint16, int16, uint32, int32 or float32 in byte order ABCD, CDAB, BADC or DCBA (find the right order with decode_registers first). Coils and discrete inputs report true/false. Modbus has no subscriptions, so every interval is one read; keep the interval reasonable for the device.")]
    public async Task<CallToolResult> WatchAddress(
        [Description("Device name from list_devices.")] string device,
        [Description("Start address, e.g. hr:0, ir:12, coil:5 or 40001.")] string address,
        [Description("Number of values to watch (1-16).")] int count = 1,
        [Description("Register value type: uint16, int16, uint32, int32 or float32. Ignored for coils and discrete inputs.")] string type = "uint16",
        [Description("Byte order for 32-bit types: ABCD, CDAB, BADC or DCBA.")] string byteOrder = "ABCD",
        [Description("How long to watch, in seconds (1-60).")] int durationSeconds = 10,
        [Description("Time between reads, in milliseconds (200-10000).")] int intervalMs = 1000,
        CancellationToken cancellationToken = default)
    {
        var parsed = ModbusAddress.Parse(address);
        if (!parsed.IsSuccess)
            return FailWatch(device, parsed.Error);
        var isBit = parsed.Value.ObjectType is ModbusObjectType.Coil or ModbusObjectType.DiscreteInput;
        if (count is < 1 or > MaxWatchedValues)
            return FailWatch(device, Invalid($"count must be between 1 and {MaxWatchedValues}."));
        if (!isBit && !RegisterDecoder.ValueTypes.Contains(type))
            return FailWatch(device, Invalid($"type must be one of {string.Join(", ", RegisterDecoder.ValueTypes)}."));
        if (!isBit && !RegisterDecoder.IsByteOrder(byteOrder))
            return FailWatch(device, Invalid("byteOrder must be ABCD, CDAB, BADC or DCBA."));
        if (durationSeconds is < 1 or > 60)
            return FailWatch(device, Invalid("durationSeconds must be between 1 and 60."));
        if (intervalMs is < 200 or > 10_000)
            return FailWatch(device, Invalid("intervalMs must be between 200 and 10000."));

        var session = await sessions.GetModbusAsync(device, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
            return FailWatch(device, session.Error);

        var width = isBit ? 1 : RegisterDecoder.RegistersPerValue(type);
        var start = parsed.Value;
        var names = Enumerable.Range(0, count)
            .Select(i => (start with { Offset = (ushort)(start.Offset + i * width) }).ToString())
            .ToList();
        var collector = new WatchCollector(names);
        var previous = new string?[count];
        var registers = new ushort[count * width];
        var bits = new bool[count];
        var started = DateTimeOffset.UtcNow;
        var deadline = started.AddSeconds(durationSeconds);
        var reads = 0;
        var failures = 0;

        while (true)
        {
            var now = DateTime.UtcNow;
            DriverResult<int> read = isBit
                ? start.ObjectType == ModbusObjectType.Coil
                    ? await session.Value.ReadCoilsAsync(start.Offset, bits, start.UnitId, cancellationToken).ConfigureAwait(false)
                    : await session.Value.ReadDiscreteInputsAsync(start.Offset, bits, start.UnitId, cancellationToken).ConfigureAwait(false)
                : await ReadRegistersAsync(session.Value, start, registers, cancellationToken).ConfigureAwait(false);
            reads++;

            for (var i = 0; i < count; i++)
            {
                DriverSample sample;
                if (!read.IsSuccess)
                {
                    sample = new DriverSample(DriverValue.FromFloat64(0), new DriverQuality(DriverQualityLevel.Bad, DriverQualityFlags.CommunicationFailure), now, now);
                }
                else
                {
                    var value = isBit
                        ? DriverValue.FromBoolean(bits[i])
                        : RegisterDecoder.DecodeOne(registers, i * width, type, byteOrder);
                    sample = DriverSample.Good(value, now, now);
                }

                // Record changes only, like an OPC UA subscription: the first read and every new value or quality.
                var text = read.IsSuccess ? sample.Value.ToInvariantString() : null;
                if (reads > 1 && text == previous[i])
                    continue;
                previous[i] = text;
                collector.Add(WatchCollector.HandleFor(i), sample);
            }

            if (!read.IsSuccess)
                failures++;
            var wait = TimeSpan.FromMilliseconds(intervalMs);
            if (DateTimeOffset.UtcNow + wait > deadline)
                break;
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        return ToolOutput.Ok(
            new WatchResult(device, started, Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1), intervalMs, collector.Results(), null)
            {
                Reads = reads,
                FailedReads = failures
            },
            Json.WatchResult);
    }

    private static CallToolResult FailWatch(string device, DriverError error)
    {
        return ToolOutput.Failed(WatchResult.Failed(device, error), Json.WatchResult);
    }

    private static ToolJsonContext Json => ToolJsonContext.Readable;

    private static CallToolResult Fail(ReadResult result)
    {
        return ToolOutput.Failed(result, Json.ReadResult);
    }

    private static CallToolResult Fail(DecodeResult result)
    {
        return ToolOutput.Failed(result, Json.DecodeResult);
    }

    private static ValueTask<DriverResult<int>> ReadRegistersAsync(
        ModbusTcpClient client,
        ModbusAddress address,
        ushort[] destination,
        CancellationToken cancellationToken)
    {
        return address.ObjectType == ModbusObjectType.HoldingRegister
            ? client.ReadHoldingRegistersAsync(address.Offset, destination, address.UnitId, cancellationToken)
            : client.ReadInputRegistersAsync(address.Offset, destination, address.UnitId, cancellationToken);
    }

    private static DriverError Invalid(string message)
    {
        return new DriverError(DriverErrorKind.Configuration, "INVALID_ARGUMENT", message);
    }
}

public sealed record DeviceSummary(string Name, string Protocol, string Endpoint, string? Path, bool Connected);

/// <summary>A driver error as the model should see it: what failed, why, and what to try next.</summary>
public sealed record ToolError(string Code, string Kind, string Message, string? Cause, string? SuggestedAction)
{
    public static ToolError From(DriverError error)
    {
        error = OpcUaStatusCodes.Explain(error);
        return new ToolError(error.Code, error.Kind.ToString(), error.Message, error.Cause, error.SuggestedAction);
    }
}

public sealed record ReadResult(
    string Device,
    string Address,
    DateTimeOffset? TimestampUtc,
    ushort[]? Registers,
    bool[]? Bits,
    ToolError? Error)
{
    public static ReadResult Failed(string device, string address, DriverError error)
    {
        return new ReadResult(device, address, null, null, null, ToolError.From(error));
    }
}

public sealed record DecodeResult(
    string Device,
    string Address,
    DateTimeOffset? TimestampUtc,
    ushort[]? Registers,
    string[]? Hex,
    List<DecodedValue>? Interpretations,
    ToolError? Error)
{
    public static DecodeResult Failed(string device, string address, DriverError error)
    {
        return new DecodeResult(device, address, null, null, null, null, ToolError.From(error));
    }
}
