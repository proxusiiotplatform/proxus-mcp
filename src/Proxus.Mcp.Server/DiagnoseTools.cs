using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Proxus.Drivers;
using Proxus.Drivers.Modbus;
using Proxus.Drivers.OpcUa;

namespace Proxus.Mcp;

/// <summary>
///     Walks a connection layer by layer (name, TCP, protocol, security, session, data) on a fresh connection, so
///     the first failing step names the layer at fault. Read-only, like every other tool.
/// </summary>
[McpServerToolType]
public sealed class DiagnoseTools(DeviceSessions sessions, PkiStore pki)
{
    private static readonly IndustrialNodeId CurrentTime = IndustrialNodeId.Numeric(0, 2258);
    private static readonly IndustrialNodeId ServerState = IndustrialNodeId.Numeric(0, 2259);
    private static readonly IndustrialNodeId ProductName = IndustrialNodeId.Numeric(0, 2261);
    private static readonly IndustrialNodeId ManufacturerName = IndustrialNodeId.Numeric(0, 2263);
    private static readonly IndustrialNodeId SoftwareVersion = IndustrialNodeId.Numeric(0, 2264);
    private static readonly string[] ServerStates = ["Running", "Failed", "NoConfiguration", "Suspended", "Shutdown", "Test", "CommunicationFault", "Unknown"];

    [McpServerTool(Name = "diagnose_connection", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Finds out why a device cannot be reached or read. Checks, in order: host name resolution, TCP port, protocol handshake, security and certificates (OPC UA), login, a test read and the device's identity and clock. Each step reports ok, warning, failed or skipped with a plain explanation; the first failed step is the cause, and its next_step says what to do. Use it whenever another tool returns a connection, timeout, certificate or login error.")]
    public async Task<CallToolResult> DiagnoseConnection(
        [Description("Device name from list_devices.")] string device,
        CancellationToken cancellationToken = default)
    {
        if (!sessions.Configuration.Devices.TryGetValue(device, out var settings))
        {
            var unknown = new DiagnoseResult(device, null, null, false,
                [Step.Failed("configuration", 0, $"No device named '{device}'.", "Call list_devices to see the configured devices.")],
                $"'{device}' is not in the configuration.");
            return ToolOutput.Failed(unknown, ToolJsonContext.Readable.DiagnoseResult);
        }

        var steps = settings.IsModbus
            ? await DiagnoseModbusAsync(settings, cancellationToken).ConfigureAwait(false)
            : await DiagnoseOpcUaAsync(settings, cancellationToken).ConfigureAwait(false);

        var failed = steps.FirstOrDefault(static s => s.Status == "failed");
        var warnings = steps.Count(static s => s.Status == "warning");
        var summary = failed is not null
            ? $"Fails at '{failed.Name}': {failed.Detail}"
            : warnings > 0
                ? $"Reachable and readable, with {warnings} warning(s)."
                : "Reachable and readable; every check passed.";
        var result = new DiagnoseResult(device, settings.Protocol, settings.DisplayEndpoint, failed is null, steps, summary);
        // A failed diagnosis is still a successful tool call: the report is the answer.
        return ToolOutput.Ok(result, ToolJsonContext.Readable.DiagnoseResult);
    }

    private static async Task<List<Step>> DiagnoseModbusAsync(DeviceConfiguration settings, CancellationToken cancellationToken)
    {
        var steps = new List<Step>();
        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);
        if (!await NetworkStepsAsync(steps, settings.Host, settings.Port, timeout, cancellationToken).ConfigureAwait(false))
            return Skip(steps, "modbus_request", "device_identity");

        await using var client = new ModbusTcpClient(new ModbusTcpOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            UnitId = settings.UnitId,
            ConnectTimeout = timeout,
            RequestTimeout = timeout
        });

        var watch = Stopwatch.StartNew();
        var connected = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var registers = new ushort[1];
        var read = connected.IsSuccess
            ? await client.ReadHoldingRegistersAsync(0, registers, cancellationToken: cancellationToken).ConfigureAwait(false)
            : DriverResult<int>.Failure(connected.Error);
        var elapsed = watch.ElapsedMilliseconds;

        if (read.IsSuccess)
        {
            steps.Add(Step.Ok("modbus_request", elapsed, $"Unit {settings.UnitId} answered a holding-register read (hr:0 = {registers[0]}) in {elapsed} ms."));
        }
        else if (read.Error.Code == "MODBUS_EXCEPTION" && read.Error.NativeCode is 2 or 1)
        {
            // An exception reply proves the device is alive and speaks Modbus; address 0 just isn't mapped.
            steps.Add(Step.Ok("modbus_request", elapsed,
                $"Unit {settings.UnitId} answered in {elapsed} ms, rejecting the test read of hr:0 with exception {read.Error.NativeCode} ({read.Error.NativeMeaning}). " +
                "The device is alive and speaks Modbus; hr:0 is just not in its map. Use the addresses from its register map."));
        }
        else if (read.Error.Code == "MODBUS_EXCEPTION" && read.Error.NativeCode is 10 or 11)
        {
            steps.Add(Step.Failed("modbus_request", elapsed,
                $"The TCP device is a gateway and could not reach serial unit {settings.UnitId} ({read.Error.NativeMeaning}).",
                "Check the serial side: unit_id, baud rate, parity, wiring and the gateway's serial timeout."));
        }
        else if (read.Error.Kind == DriverErrorKind.Timeout)
        {
            steps.Add(Step.Failed("modbus_request", elapsed,
                $"TCP port {settings.Port} is open but nothing answered a Modbus request within {settings.TimeoutMs} ms.",
                $"Most often unit_id {settings.UnitId} is wrong (the device manual names it; 0, 1 and 255 are common), the port belongs to another service, " +
                "or a serial gateway is waiting on a silent device."));
        }
        else
        {
            steps.Add(Step.Failed("modbus_request", elapsed, read.Error.Message, read.Error.SuggestedAction));
        }

        if (steps[^1].Status == "failed")
            return Skip(steps, "device_identity");

        watch.Restart();
        var identity = await client.ReadDeviceIdentificationAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var names = identity.IsSuccess
            ? identity.Value.Objects.Select(static o => o.AsAscii().Trim('\0', ' ')).Where(static s => s.Length > 0).ToArray()
            : [];
        if (names.Length > 0)
        {
            steps.Add(Step.Ok("device_identity", watch.ElapsedMilliseconds, $"Device reports: {string.Join(" / ", names)}."));
        }
        else
        {
            steps.Add(Step.Skipped("device_identity", "The device reports no identity (Read Device Identification, function 43/14); many do not. Not a problem."));
        }

        return steps;
    }

    private async Task<List<Step>> DiagnoseOpcUaAsync(DeviceConfiguration settings, CancellationToken cancellationToken)
    {
        var steps = new List<Step>();
        var endpoint = new Uri(settings.Endpoint!);
        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);
        if (!await NetworkStepsAsync(steps, endpoint.Host, endpoint.Port, timeout, cancellationToken).ConfigureAwait(false))
            return Skip(steps, "opcua_endpoints", "security", "session", "server_status");

        var watch = Stopwatch.StartNew();
        var discovered = await OpcUaSecurity.DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);
        if (!discovered.IsSuccess)
        {
            steps.Add(Step.Failed("opcua_endpoints", watch.ElapsedMilliseconds,
                $"TCP is open but the OPC UA handshake failed: {discovered.Error.Message}",
                "Check that the port belongs to an OPC UA server and the endpoint path is right (e.g. opc.tcp://host:4840 or the vendor's documented path)."));
            return Skip(steps, "security", "session", "server_status");
        }

        var offered = discovered.Value
            .Select(static e => $"{OpcUaSecurity.PolicyName(e.SecurityPolicyUri)}/{e.SecurityMode}")
            .Distinct()
            .ToArray();
        var advertisedHosts = discovered.Value
            .Select(static e => Uri.TryCreate(e.EndpointUrl, UriKind.Absolute, out var u) ? u.Host : null)
            .Where(static h => h is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hostNote = advertisedHosts.Length > 0 && !advertisedHosts.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase)
            ? $" The server calls itself {string.Join(", ", advertisedHosts!)}; this server connects to {endpoint.Host} anyway, as configured."
            : string.Empty;
        steps.Add(Step.Ok("opcua_endpoints", watch.ElapsedMilliseconds,
            $"Server answered with {discovered.Value.Length} endpoint(s): {string.Join(", ", offered)}.{hostNote}"));

        var options = OpcUaSecurity.BuildOptions(settings, pki, discovered.Value);
        if (!options.IsSuccess)
        {
            steps.Add(Step.Failed("security", 0, options.Error.Message, options.Error.SuggestedAction));
            return Skip(steps, "session", "server_status");
        }

        steps.Add(SecurityStep(settings, options.Value, offered));
        if (steps[^1].Status == "failed")
            return Skip(steps, "session", "server_status");

        watch.Restart();
        await using var client = new OpcUaTcpClient(options.Value);
        var connected = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var session = connected.IsSuccess
            ? await DeviceSessions.OpenSessionAsync(client, settings, cancellationToken).ConfigureAwait(false)
            : connected;
        if (!session.IsSuccess)
        {
            var explained = settings.IsUnsecured ? OpcUaStatusCodes.Explain(session.Error) : OpcUaSecurity.ExplainRejection(session.Error, pki);
            steps.Add(Step.Failed("session", watch.ElapsedMilliseconds, explained.Message, explained.SuggestedAction));
            return Skip(steps, "server_status");
        }

        var login = string.IsNullOrEmpty(settings.Username) ? "anonymous" : $"user '{settings.Username}'";
        steps.Add(Step.Ok("session", watch.ElapsedMilliseconds, $"Session open with {login} login."));

        watch.Restart();
        var nodes = new[] { ServerState, CurrentTime, ProductName, ManufacturerName, SoftwareVersion }
            .Select(static n => new OpcUaNodeDescriptor(n))
            .ToArray();
        var samples = new DriverSample[nodes.Length];
        var read = await client.ReadAsync(nodes, samples, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            var explained = OpcUaStatusCodes.Explain(read.Error);
            steps.Add(Step.Failed("server_status", watch.ElapsedMilliseconds, explained.Message, explained.SuggestedAction));
            return steps;
        }

        steps.Add(ServerStatusStep(samples, watch.ElapsedMilliseconds));
        steps.Add(ClockStep(samples[1]));
        return steps;
    }

    private Step SecurityStep(DeviceConfiguration settings, OpcUaTcpOptions options, string[] offered)
    {
        if (settings.IsUnsecured)
        {
            var secureOffered = offered.Any(static o => !o.StartsWith("None", StringComparison.Ordinal));
            return Step.Warning("security", 0,
                "The connection is unsecured: values and the login travel unencrypted and the server is not authenticated.",
                secureOffered
                    ? "The server offers secure endpoints. Set security_mode to sign_and_encrypt for this device."
                    : null);
        }

        var server = options.EndpointServerCertificate!;
        if (settings.CheckHostName && HostNameMismatch(server, new Uri(settings.Endpoint!)) is { } mismatch)
            return mismatch;

        var (own, applicationUri) = pki.GetOrCreateApplicationCertificate();
        var expiresSoon = new[] { server, own }.Where(static c => c.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(30)).ToArray();
        var detail = $"{settings.SecurityPolicy}/{settings.SecurityMode}. Server certificate '{server.GetNameInfo(X509NameType.SimpleName, false)}' " +
                     $"is trusted (valid until {server.NotAfter:yyyy-MM-dd}). This client is {applicationUri}, SHA-256 {PkiStore.Sha256Thumbprint(own)[..16]}…";
        return expiresSoon.Length > 0
            ? Step.Warning("security", 0, detail + $" A certificate expires within 30 days: {string.Join(", ", expiresSoon.Select(static c => c.Subject))}.",
                "Renew it before it expires, or the connection will stop.")
            : Step.Ok("security", 0, detail);
    }

    /// <summary>
    ///     The check the driver makes when it connects, made here first so the report names the security layer and
    ///     the names the certificate does carry.
    /// </summary>
    private static Step? HostNameMismatch(X509Certificate2 certificate, Uri endpoint)
    {
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(static e => e.EnumerateDnsNames().Concat(e.EnumerateIPAddresses().Select(static a => a.ToString())))
            .ToArray();
        var host = endpoint.Host;
        var matches = names.Any(name =>
            string.Equals(name, host, StringComparison.OrdinalIgnoreCase) ||
            (name.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(name[1..], StringComparison.OrdinalIgnoreCase)) ||
            (IPAddress.TryParse(name, out var a) && IPAddress.TryParse(host, out var b) && a.Equals(b)));
        if (matches || names.Length == 0)
            return null;

        var suggestion = names.FirstOrDefault(static n => !IPAddress.TryParse(n, out _)) ?? names[0];
        return Step.Failed("security", 0,
            $"The server certificate names {string.Join(", ", names)}, but the endpoint uses '{host}', so the secure connection would be refused.",
            $"Use opc.tcp://{suggestion}:{endpoint.Port}{endpoint.AbsolutePath} as the endpoint, or set check_host_name to false for this device if '{host}' is the right address.");
    }

    private static Step ServerStatusStep(DriverSample[] samples, long elapsed)
    {
        string? Text(int i) => samples[i].Quality.Level == DriverQualityLevel.Good ? samples[i].Value.ToInvariantString() : null;

        var identity = string.Join(" ", new[] { Text(3), Text(2), Text(4) is { } v ? $"v{v}" : null }.Where(static s => !string.IsNullOrWhiteSpace(s)));
        var stateText = Text(0);
        var state = int.TryParse(stateText, out var s) && s >= 0 && s < ServerStates.Length ? ServerStates[s] : stateText ?? "unknown";
        var detail = $"Read ServerStatus in {elapsed} ms. State: {state}." + (identity.Length > 0 ? $" Server: {identity}." : string.Empty);
        return state == "Running"
            ? Step.Ok("server_status", elapsed, detail)
            : Step.Warning("server_status", elapsed, detail, "The server is not in the Running state; values may be stale or Bad until it is.");
    }

    private static Step ClockStep(DriverSample currentTime)
    {
        if (currentTime.Quality.Level != DriverQualityLevel.Good || currentTime.Value.Type != IndustrialDataType.DateTime)
            return Step.Skipped("clock", "The server did not report its current time.");

        var skew = currentTime.Value.AsDateTime().ToUniversalTime() - DateTime.UtcNow;
        var seconds = Math.Abs(skew.TotalSeconds);
        var detail = $"Server clock differs from this machine by {seconds:0.#} s.";
        return seconds > 300
            ? Step.Warning("clock", 0, detail,
                "Large clock differences break certificate checks and make timestamps misleading. Synchronise both machines with NTP.")
            : Step.Ok("clock", 0, detail);
    }

    /// <summary>Name resolution and TCP connect. Returns false when the device is not reachable at all.</summary>
    private static async Task<bool> NetworkStepsAsync(
        List<Step> steps,
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
            steps.Add(Step.Ok("resolve", 0, $"{host} is an IP address; no name lookup needed."));
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                steps.Add(Step.Ok("resolve", watch.ElapsedMilliseconds, $"{host} resolves to {string.Join(", ", addresses.Select(static a => a.ToString()))}."));
            }
            catch (SocketException exception)
            {
                steps.Add(Step.Failed("resolve", watch.ElapsedMilliseconds, $"Host name '{host}' cannot be resolved ({exception.SocketErrorCode}).",
                    "Check the spelling, use the IP address, or add the name to DNS or the hosts file of the machine running this server."));
                Skip(steps, "tcp");
                return false;
            }
        }

        watch.Restart();
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(addresses, port, deadline.Token).ConfigureAwait(false);
            steps.Add(Step.Ok("tcp", watch.ElapsedMilliseconds, $"TCP port {port} on {host} accepted the connection in {watch.ElapsedMilliseconds} ms."));
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            steps.Add(Step.Failed("tcp", watch.ElapsedMilliseconds, $"No answer from {host}:{port} within {timeout.TotalMilliseconds:0} ms.",
                "The device is off, unreachable from this machine, or a firewall silently drops the traffic. Check the network path, VLAN/routing and firewall rules; ping the device."));
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            steps.Add(Step.Failed("tcp", watch.ElapsedMilliseconds, $"{host} is reachable but refused port {port}.",
                $"Nothing listens on port {port}: the service is stopped, disabled, or uses another port (Modbus TCP 502, OPC UA 4840 are the defaults)."));
        }
        catch (SocketException exception)
        {
            steps.Add(Step.Failed("tcp", watch.ElapsedMilliseconds, $"TCP connection to {host}:{port} failed: {exception.SocketErrorCode}.",
                "Check the network route to the device (HostUnreachable/NetworkUnreachable usually means a wrong subnet or gateway)."));
        }

        return false;
    }

    private static List<Step> Skip(List<Step> steps, params string[] names)
    {
        foreach (var name in names)
            steps.Add(Step.Skipped(name, "Not run: an earlier step failed."));
        return steps;
    }
}

public sealed record Step(string Name, string Status, long? DurationMs, string Detail, string? NextStep)
{
    public static Step Ok(string name, long ms, string detail) => new(name, "ok", ms, detail, null);
    public static Step Warning(string name, long ms, string detail, string? next) => new(name, "warning", ms, detail, next);
    public static Step Failed(string name, long ms, string detail, string? next) => new(name, "failed", ms, detail, next);
    public static Step Skipped(string name, string detail) => new(name, "skipped", null, detail, null);
}

public sealed record DiagnoseResult(string Device, string? Protocol, string? Endpoint, bool Healthy, List<Step> Steps, string Summary);
