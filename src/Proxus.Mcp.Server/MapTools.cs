using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Proxus.Drivers;
using Proxus.Drivers.Modbus;

namespace Proxus.Mcp;

/// <summary>Register maps: find a device's map, look inside it, and read registers by name. Read-only.</summary>
[McpServerToolType]
public sealed class MapTools(DeviceSessions sessions)
{
    private const int MaxSearchResults = 25;
    private const int MaxListedRegisters = 200;
    private const int MaxNamedReads = 32;

    private DeviceMapLibrary Library => sessions.Configuration.Maps;

    [McpServerTool(Name = "find_device_map", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the register map library by vendor, model, alias or category (e.g. 'PM5560', 'schneider drive', 'SDM630'). Returns map ids with a summary and whether the map was checked on real hardware. Use the id with get_device_map, or set it as the device's map in the configuration to use read_named.")]
    public CallToolResult FindDeviceMap(
        [Description("Words to look for; every word must match.")] string query)
    {
        if (Library.Count == 0)
            return ToolOutput.Failed(new MapSearchResult(query, 0, [], ToolError.From(NoLibrary())), Json.MapSearchResult);

        var hits = Library.Search(query);
        var items = hits.Take(MaxSearchResults)
            .Select(map => new MapSummary(map.Id, map.Vendor, map.Model, map.Aliases, map.Category, map.Summary,
                map.Registers.Count, map.Verified.Count > 0))
            .ToList();
        return ToolOutput.Ok(new MapSearchResult(query, hits.Count, items, null), Json.MapSearchResult);
    }

    [McpServerTool(Name = "get_device_map", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows registers from a register map: key, name, address (in read_address notation), type, byte order, unit and scale, plus the map's connection notes and sources. Give a map id or the name of a configured device that has a map. Filter with search (matches key, name or group) to keep the list short; large maps have thousands of registers.")]
    public CallToolResult GetDeviceMap(
        [Description("Map id (from find_device_map) or a device name from list_devices.")] string mapOrDevice,
        [Description("Optional words that the key, name or group must contain, e.g. 'voltage' or 'energy import'.")] string? search = null,
        [Description("Maximum registers to return (1-200).")] int limit = 50)
    {
        var map = Resolve(mapOrDevice, out var error);
        if (map is null)
            return ToolOutput.Failed(DeviceMapView.Failed(mapOrDevice, error!.Value), Json.DeviceMapView);
        if (limit is < 1 or > MaxListedRegisters)
            return ToolOutput.Failed(DeviceMapView.Failed(mapOrDevice, Invalid($"limit must be between 1 and {MaxListedRegisters}.")), Json.DeviceMapView);

        var words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matching = map.Registers
            .Where(r => words.All(w =>
                r.Key.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                (r.Group?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
        var registers = matching.Take(limit)
            .Select(r => new MapRegisterView(r.Key, r.Name, r.Group, r.ToolAddress, r.Type, r.Count,
                r.Count > 1 && r.Type is not ("string" or "bitfield" or "raw") ? r.ByteOrder ?? map.DefaultByteOrder : null,
                r.Unit, r.Scale, r.ScaleFactor, r.Access, r.Values, r.AppliesTo, r.Notes))
            .ToList();
        return ToolOutput.Ok(new DeviceMapView(map.Id, map.Vendor, map.Model, map.Summary, map.Connection.Notes,
            map.Sources.Select(s => $"{s.Publisher}: {s.Title}{(s.Version is null ? "" : $" ({s.Version})")} {s.Url}").ToList(),
            map.Verified.Count > 0, map.Registers.Count, matching.Count, registers, null), Json.DeviceMapView);
    }

    [McpServerTool(Name = "read_named", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads registers by their key in the device's register map and returns engineering values: the map supplies address, type, byte order, scale and unit, so there is no guessing. Find keys with get_device_map. Only reads; nothing is written. Values are as reliable as the map: check the first reading against the device display, especially for maps not checked on hardware.")]
    public async Task<CallToolResult> ReadNamed(
        [Description("Device name from list_devices.")] string device,
        [Description("Register keys from the map, e.g. [\"voltage_l1_n\", \"active_power_total\"] (1-32).")] string[] keys,
        [Description("Map id to use when the device has none configured.")] string? map = null,
        CancellationToken cancellationToken = default)
    {
        if (!sessions.Configuration.Devices.TryGetValue(device, out var configuration))
            return FailNamed(device, null, new DriverError(DriverErrorKind.Configuration, "UNKNOWN_DEVICE",
                $"No device named '{device}'. Call list_devices to see the configured devices."));
        var deviceMap = map is null ? configuration.ResolvedMap : Library.Find(map);
        if (deviceMap is null)
            return FailNamed(device, map, map is null
                ? Invalid($"Device '{device}' has no register map. Pass map, or set map in the configuration (find_device_map looks up ids).")
                : Invalid($"No register map '{map}'. Use find_device_map to look up the id."));
        if (keys.Length is < 1 or > MaxNamedReads)
            return FailNamed(device, deviceMap.Id, Invalid($"keys must name between 1 and {MaxNamedReads} registers."));

        var byKey = deviceMap.Registers.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        var unknown = keys.Where(k => !byKey.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
            return FailNamed(device, deviceMap.Id, Invalid($"Unknown keys: {string.Join(", ", unknown)}. Use get_device_map to find the keys."));

        var session = await sessions.GetModbusAsync(device, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
            return FailNamed(device, deviceMap.Id, session.Error);

        var unitId = configuration.UnitId;
        var exponents = new Dictionary<string, DriverResult<int>>(StringComparer.OrdinalIgnoreCase);
        var values = new List<NamedValue>();
        foreach (var key in keys)
        {
            var register = byKey[key];
            values.Add(await ReadOneAsync(session.Value, unitId, deviceMap, register, byKey, exponents, cancellationToken).ConfigureAwait(false));
        }

        var anyGood = values.Any(v => v.Error is null);
        var result = new NamedReadResult(device, deviceMap.Id, DateTimeOffset.UtcNow, values,
            deviceMap.Verified.Count > 0 ? null : "This map is transcribed from vendor documents and not yet checked on hardware.", null);
        return anyGood ? ToolOutput.Ok(result, Json.NamedReadResult) : ToolOutput.Failed(result, Json.NamedReadResult);
    }

    private static async Task<NamedValue> ReadOneAsync(
        ModbusTcpClient client,
        byte unitId,
        DeviceMap map,
        MapRegister register,
        Dictionary<string, MapRegister> byKey,
        Dictionary<string, DriverResult<int>> exponents,
        CancellationToken cancellationToken)
    {
        NamedValue Fail(DriverError error) => new(register.Key, register.Name, register.ToolAddress, register.Type, null, register.Unit, null, null, ToolError.From(error));

        if (register.Access == "w")
            return Fail(Invalid("The map marks this register write-only; it cannot be read."));

        if (register.Table is "coil" or "discrete_input")
        {
            var bits = new bool[1];
            var readBits = register.Table == "coil"
                ? await client.ReadCoilsAsync((ushort)register.Address, bits, unitId, cancellationToken).ConfigureAwait(false)
                : await client.ReadDiscreteInputsAsync((ushort)register.Address, bits, unitId, cancellationToken).ConfigureAwait(false);
            return readBits.IsSuccess
                ? new NamedValue(register.Key, register.Name, register.ToolAddress, register.Type, bits[0] ? "true" : "false", null, null, null, null)
                : Fail(readBits.Error);
        }

        var registers = new ushort[register.Count];
        var read = await ReadAsync(client, register.Table, register.Address, registers, unitId, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Fail(read.Error);

        var hex = MapValueDecoder.Hex(registers);
        if (register.Type == "string")
            return new NamedValue(register.Key, register.Name, register.ToolAddress, register.Type,
                MapValueDecoder.Text(registers, register.ByteOrder ?? "ABCD"), null, hex, null, null);

        var byteOrder = register.ByteOrder ?? map.DefaultByteOrder;
        var number = MapValueDecoder.Number(register, registers, byteOrder);
        if (number is null)
            return new NamedValue(register.Key, register.Name, register.ToolAddress, register.Type, null, register.Unit, hex,
                null, null) { Note = "Vendor-specific encoding: interpret the raw registers with the map's notes." };

        int? exponent = null;
        if (register.ScaleFactor is { } scaleFactorKey && byKey.TryGetValue(scaleFactorKey, out var scaleFactor))
        {
            if (!exponents.TryGetValue(scaleFactorKey, out var cached))
            {
                var sf = new ushort[1];
                var sfRead = await ReadAsync(client, scaleFactor.Table, scaleFactor.Address, sf, unitId, cancellationToken).ConfigureAwait(false);
                cached = sfRead.IsSuccess ? DriverResult<int>.Success((short)sf[0]) : DriverResult<int>.Failure(sfRead.Error);
                exponents[scaleFactorKey] = cached;
            }

            if (!cached.IsSuccess)
                return Fail(cached.Error);
            exponent = cached.Value;
        }

        var value = MapValueDecoder.Scaled(number.Value, register.Scale, exponent);
        string? label = null;
        if (register.Values is { } labels)
            labels.TryGetValue(((long)number.Value).ToString(System.Globalization.CultureInfo.InvariantCulture), out label);
        return new NamedValue(register.Key, register.Name, register.ToolAddress, register.Type, MapValueDecoder.Format(value),
            register.Unit, hex, label, null);
    }

    private static ValueTask<DriverResult<int>> ReadAsync(ModbusTcpClient client, string table, int address, ushort[] destination,
        byte unitId, CancellationToken cancellationToken)
    {
        return table == "input"
            ? client.ReadInputRegistersAsync((ushort)address, destination, unitId, cancellationToken)
            : client.ReadHoldingRegistersAsync((ushort)address, destination, unitId, cancellationToken);
    }

    private DeviceMap? Resolve(string mapOrDevice, out DriverError? error)
    {
        error = null;
        if (sessions.Configuration.Devices.TryGetValue(mapOrDevice, out var device))
        {
            if (device.ResolvedMap is not null)
                return device.ResolvedMap;
            error = Invalid($"Device '{mapOrDevice}' has no register map configured. Use find_device_map to look one up.");
            return null;
        }

        var map = Library.Find(mapOrDevice);
        if (map is null)
            error = Library.Count == 0 ? NoLibrary() : Invalid($"No register map or device named '{mapOrDevice}'. Use find_device_map to look up map ids.");
        return map;
    }

    private static DriverError NoLibrary()
    {
        return new DriverError(DriverErrorKind.Configuration, "NO_MAPS", "No register maps are loaded.")
        {
            SuggestedAction = "Set maps_dir in the configuration to a folder of register maps (JSON from the Modbus Register Maps collection)."
        };
    }

    private static CallToolResult FailNamed(string device, string? map, DriverError error)
    {
        return ToolOutput.Failed(new NamedReadResult(device, map, null, [], null, ToolError.From(error)), Json.NamedReadResult);
    }

    private static DriverError Invalid(string message)
    {
        return new DriverError(DriverErrorKind.Configuration, "INVALID_ARGUMENT", message);
    }

    private static ToolJsonContext Json => ToolJsonContext.Readable;
}

public sealed record MapSummary(
    string Id, string Vendor, string Model, List<string> Aliases, string Category, string Summary, int Registers, bool HardwareVerified);

public sealed record MapSearchResult(string Query, int Matches, List<MapSummary> Maps, ToolError? Error);

public sealed record MapRegisterView(
    string Key, string Name, string? Group, string Address, string Type, int Registers, string? ByteOrder, string? Unit,
    double? Scale, string? ScaleFactor, string Access, Dictionary<string, string>? Values, List<string>? AppliesTo, string? Notes);

public sealed record DeviceMapView(
    string Map, string? Vendor, string? Model, string? Summary, List<string>? ConnectionNotes, List<string>? Sources,
    bool? HardwareVerified, int? TotalRegisters, int? Matching, List<MapRegisterView>? Registers, ToolError? Error)
{
    public static DeviceMapView Failed(string map, DriverError error)
    {
        return new DeviceMapView(map, null, null, null, null, null, null, null, null, null, ToolError.From(error));
    }
}

/// <param name="Value">Engineering value (scale and scale factor applied), or text for strings.</param>
/// <param name="Raw">The registers as read, in hex, for checking the decoding.</param>
/// <param name="Label">Meaning of an enumerated value, when the map lists it.</param>
public sealed record NamedValue(
    string Key, string Name, string Address, string Type, string? Value, string? Unit, string? Raw, string? Label, ToolError? Error)
{
    public string? Note { get; init; }
}

public sealed record NamedReadResult(
    string Device, string? Map, DateTimeOffset? TimestampUtc, List<NamedValue> Values, string? Caution, ToolError? Error);
