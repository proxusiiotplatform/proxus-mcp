using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Proxus.Drivers;
using Proxus.Drivers.Modbus;

namespace Proxus.Mcp;

/// <summary>Checks a register map against the real device and prepares a report the community collection accepts.</summary>
[McpServerToolType]
public sealed partial class VerifyTools(DeviceSessions sessions)
{
    private const string IssueUrl = "https://github.com/proxusiiotplatform/modbus-register-maps/issues/new";
    private const int MaxBlockRegisters = 100;
    private const int MaxUrlLength = 7500;
    private const int MaxEnergyRereads = 10;

    private static readonly string Tool = "proxus-mcp " + (typeof(VerifyTools).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");

    [McpServerTool(Name = "verify_device_map", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Checks the device's register map against the real device and prepares a hardware verification report for the open Modbus Register Maps collection. Only reads: every map register is read once (blocks, rate-limited), then automatic checks run (registers answer, floats plausible, frequency, voltage and power factor in range, energy counters not running backwards, model matches). " +
                 "Flow: call it once, show the user the summary, then ask them to read two or three values off the device's display (e.g. a voltage and the frequency) and call it again with those in confirmations to reach level L3. " +
                 "Nothing is sent anywhere: the result has a submitUrl the USER opens to file the report under their own GitHub account. Never claim it was submitted. The report holds check results only, no addresses or host names.")]
    public async Task<CallToolResult> VerifyDeviceMap(
        [Description("Device name from list_devices.")] string device,
        [Description("Values the user read off the device's display, as {\"key\": \"voltage_l1_n\", \"deviceShows\": \"231.2\"} (up to 5).")] Confirmation[]? confirmations = null,
        [Description("Map id when the device has none configured.")] string? map = null,
        [Description("Time budget in seconds (10-300). Large maps on slow links may need more.")] int maxSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        if (!sessions.Configuration.Devices.TryGetValue(device, out var configuration))
            return Fail(device, new DriverError(DriverErrorKind.Configuration, "UNKNOWN_DEVICE",
                $"No device named '{device}'. Call list_devices to see the configured devices."));
        var deviceMap = map is null ? configuration.ResolvedMap : sessions.Configuration.Maps.Find(map);
        if (deviceMap is null)
            return Fail(device, Invalid(map is null
                ? $"Device '{device}' has no register map. Pass map, or set map in the configuration (find_device_map looks up ids)."
                : $"No register map '{map}'. Use find_device_map to look up the id."));
        if (maxSeconds is < 10 or > 300)
            return Fail(device, Invalid("maxSeconds must be between 10 and 300."));
        confirmations ??= [];
        if (confirmations.Length > 5)
            return Fail(device, Invalid("Give at most 5 confirmations."));
        var byKey = deviceMap.Registers.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var c in confirmations)
        {
            if (!byKey.ContainsKey(c.Key))
                return Fail(device, Invalid($"Unknown key '{c.Key}' in confirmations. Use get_device_map to find keys."));
            if (!DisplayValue().IsMatch(c.DeviceShows.Trim()))
                return Fail(device, Invalid($"deviceShows for '{c.Key}' must be a plain number such as 231.2, as the display shows it."));
        }

        var session = await sessions.GetModbusAsync(device, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
            return Fail(device, session.Error);

        var client = session.Value;
        var unitId = configuration.UnitId;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(maxSeconds);
        var started = DateTimeOffset.UtcNow;
        var readings = new List<RegisterReading>();
        var candidates = deviceMap.Registers.Where(r => r.Access != "w").OrderBy(r => r.Table).ThenBy(r => r.Address).ToList();
        var partial = false;

        foreach (var block in Blocks(candidates))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                partial = true;
                break;
            }

            var result = await ReadBlockAsync(client, unitId, block, readings, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
                return Fail(device, result.Error);
        }

        // Energy counters read again at the end: they may only stay or grow.
        var rereads = new Dictionary<string, (double, double)>();
        foreach (var reading in readings.Where(r => r.Registers is not null && MapVerifier.IsEnergyCounter(r.Register) &&
                                                    !MapVerifier.IsNotAvailable(r.Register, r.Registers!)).Take(MaxEnergyRereads))
        {
            var again = new ushort[reading.Register.Count];
            var read = await ReadAsync(client, reading.Register.Table, reading.Register.Address, again, unitId, cancellationToken).ConfigureAwait(false);
            var byteOrder = reading.Register.ByteOrder ?? deviceMap.DefaultByteOrder;
            if (read.IsSuccess &&
                MapValueDecoder.Number(reading.Register, reading.Registers!, byteOrder) is { } first &&
                MapValueDecoder.Number(reading.Register, again, byteOrder) is { } second)
                rereads[reading.Register.Key] = (first, second);
        }

        var confirmed = new List<(string, string, double?)>();
        foreach (var c in confirmations)
        {
            var register = byKey[c.Key];
            var value = await ReadValueAsync(client, unitId, deviceMap, register, byKey, cancellationToken).ConfigureAwait(false);
            confirmed.Add((register.Key, c.DeviceShows.Trim(), value));
        }

        var report = MapVerifier.Evaluate(deviceMap, readings, confirmed, rereads, Tool, DateOnly.FromDateTime(DateTime.UtcNow));
        var reportJson = MapVerifier.ToJson(report);
        string? submitUrl = null;
        string? submitNote = null;
        if (deviceMap.ContentSha256 is null)
            submitNote = "This map has no content hash (it did not come from the collection's build), so it cannot be submitted.";
        else if (report.Level == "L0")
            submitNote = "Too many registers were refused for a verification. The map probably does not fit this device or firmware: report it as a map problem with the unreadable keys.";
        else if (partial)
            submitNote = "The time budget ran out before every register was read; call again with a larger maxSeconds before submitting.";
        else
        {
            submitUrl = SubmitUrl(deviceMap.Id, reportJson);
            if (submitUrl is null)
                submitNote = $"The report is too long for a link. The user opens {IssueUrl}?template=verification.yml and pastes the report.";
        }

        var mismatches = confirmed.Where(c => c.Item3 is { } v && !MapVerifier.WithinTolerance(c.Item2, v)).Select(c => c.Item1).ToList();
        var next = report.Level switch
        {
            "L3" => "Done. Show the user the summary and the submitUrl; they open it and press Submit.",
            "L2" when confirmed.Count == 0 => "Ask the user to read two values off the device's display (a voltage and the frequency work well), then call again with confirmations.",
            "L2" when mismatches.Count > 0 => $"The display disagrees for {string.Join(", ", mismatches)}: the map's scale, unit or address may be wrong. Tell the user; they can still submit the report.",
            "L2" => "One more confirmed value reaches L3.",
            "L1" => "Some automatic checks failed (see checks): the map may have a wrong byte order, scale or address. The report is still useful; submitting it lets the maintainers fix the map.",
            _ => "Most registers were refused: this map likely does not fit the device. Tell the user."
        };

        return ToolOutput.Ok(new VerifyResult(device, deviceMap.Id, report.Level, report.Registers.Checked, report.Registers.Read,
            report.Registers.UnreadableCount, report.Registers.NotAvailable, Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            report.Checks, report.Confirmed, report.Registers.Unreadable.Take(10).ToList(), next, submitUrl, submitNote, reportJson, null),
            ToolJsonContext.Readable.VerifyResult);
    }

    /// <summary>Runs of consecutive registers in one table, so one request reads many.</summary>
    private static IEnumerable<List<MapRegister>> Blocks(List<MapRegister> registers)
    {
        var block = new List<MapRegister>();
        foreach (var register in registers)
        {
            var bits = register.Table is "coil" or "discrete_input";
            if (block.Count > 0)
            {
                var last = block[^1];
                var span = register.Address + register.Count - block[0].Address;
                var contiguous = last.Table == register.Table && register.Address == last.Address + last.Count;
                if (bits || !contiguous || span > MaxBlockRegisters)
                {
                    yield return block;
                    block = [];
                }
            }

            block.Add(register);
            if (bits)
            {
                yield return block;
                block = [];
            }
        }

        if (block.Count > 0)
            yield return block;
    }

    private static async Task<DriverResult<int>> ReadBlockAsync(
        ModbusTcpClient client, byte unitId, List<MapRegister> block, List<RegisterReading> readings, CancellationToken cancellationToken)
    {
        var first = block[0];
        if (first.Table is "coil" or "discrete_input")
        {
            var bits = new bool[1];
            var read = first.Table == "coil"
                ? await client.ReadCoilsAsync((ushort)first.Address, bits, unitId, cancellationToken).ConfigureAwait(false)
                : await client.ReadDiscreteInputsAsync((ushort)first.Address, bits, unitId, cancellationToken).ConfigureAwait(false);
            if (IsRefusal(read))
                readings.Add(new RegisterReading(first, null, null, read.Error.NativeCode));
            else if (read.IsSuccess)
                readings.Add(new RegisterReading(first, null, bits[0], null));
            else
                return read;
            return DriverResult<int>.Success(1);
        }

        var count = block[^1].Address + block[^1].Count - first.Address;
        var buffer = new ushort[count];
        var whole = await ReadAsync(client, first.Table, first.Address, buffer, unitId, cancellationToken).ConfigureAwait(false);
        await Task.Delay(10, cancellationToken).ConfigureAwait(false); // gentle on serial gateways
        if (whole.IsSuccess)
        {
            foreach (var register in block)
                readings.Add(new RegisterReading(register, buffer.AsSpan(register.Address - first.Address, register.Count).ToArray(), null, null));
            return whole;
        }

        if (!IsRefusal(whole))
            return whole;

        // The device refused the block: find out which registers it refuses.
        foreach (var register in block)
        {
            var single = new ushort[register.Count];
            var read = block.Count == 1 ? whole : await ReadAsync(client, register.Table, register.Address, single, unitId, cancellationToken).ConfigureAwait(false);
            if (read.IsSuccess)
                readings.Add(new RegisterReading(register, single, null, null));
            else if (IsRefusal(read))
                readings.Add(new RegisterReading(register, null, null, read.Error.NativeCode));
            else
                return read;
        }

        return DriverResult<int>.Success(block.Count);
    }

    /// <summary>A Modbus exception means the device answered but refused this request; anything else is a link problem.</summary>
    private static bool IsRefusal(DriverResult<int> result)
    {
        return !result.IsSuccess && result.Error.Code == "MODBUS_EXCEPTION";
    }

    private static async Task<double?> ReadValueAsync(
        ModbusTcpClient client, byte unitId, DeviceMap map, MapRegister register, Dictionary<string, MapRegister> byKey, CancellationToken cancellationToken)
    {
        if (register.Table is "coil" or "discrete_input" or not ("holding" or "input"))
            return null;
        var registers = new ushort[register.Count];
        if (!(await ReadAsync(client, register.Table, register.Address, registers, unitId, cancellationToken).ConfigureAwait(false)).IsSuccess)
            return null;
        var number = MapValueDecoder.Number(register, registers, register.ByteOrder ?? map.DefaultByteOrder);
        if (number is null)
            return null;
        int? exponent = null;
        if (register.ScaleFactor is { } sfKey && byKey.TryGetValue(sfKey, out var sf))
        {
            var raw = new ushort[1];
            if (!(await ReadAsync(client, sf.Table, sf.Address, raw, unitId, cancellationToken).ConfigureAwait(false)).IsSuccess)
                return null;
            exponent = (short)raw[0];
        }

        return MapValueDecoder.Scaled(number.Value, register.Scale, exponent);
    }

    private static ValueTask<DriverResult<int>> ReadAsync(ModbusTcpClient client, string table, int address, ushort[] destination,
        byte unitId, CancellationToken cancellationToken)
    {
        return table == "input"
            ? client.ReadInputRegistersAsync((ushort)address, destination, unitId, cancellationToken)
            : client.ReadHoldingRegistersAsync((ushort)address, destination, unitId, cancellationToken);
    }

    /// <summary>A prefilled verification issue; GitHub reads the report field from the query string.</summary>
    public static string? SubmitUrl(string mapId, string reportJson)
    {
        var url = $"{IssueUrl}?template=verification.yml&title={Uri.EscapeDataString("Verification: " + mapId)}&report={Uri.EscapeDataString(reportJson)}";
        return url.Length <= MaxUrlLength ? url : null;
    }

    private static CallToolResult Fail(string device, DriverError error)
    {
        return ToolOutput.Failed(new VerifyResult(device, null, null, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, ToolError.From(error)),
            ToolJsonContext.Readable.VerifyResult);
    }

    private static DriverError Invalid(string message)
    {
        return new DriverError(DriverErrorKind.Configuration, "INVALID_ARGUMENT", message);
    }

    [GeneratedRegex(@"^-?[0-9]+([.,][0-9]+)?$")]
    private static partial Regex DisplayValue();
}

/// <param name="Next">What the assistant should do now.</param>
/// <param name="SubmitUrl">Opens a prefilled verification issue for the user to submit under their own account.</param>
/// <param name="Report">The report the issue carries, for the user to read before submitting.</param>
public sealed record VerifyResult(
    string Device,
    string? Map,
    string? Level,
    int Checked,
    int Read,
    int Unreadable,
    int NotAvailable,
    double Seconds,
    List<VerificationCheck>? Checks,
    List<VerificationConfirmation>? Confirmed,
    List<string>? UnreadableSample,
    string? Next,
    string? SubmitUrl,
    string? SubmitNote,
    string? Report,
    ToolError? Error);
