using Proxus.Drivers;
using Proxus.Drivers.Modbus;
using Proxus.Drivers.OpcUa;

namespace Proxus.Mcp;

/// <summary>
///     One driver connection per configured device, opened on first use and reopened after a failure.
///     The drivers serialize requests on a connection, so callers only share the connect step.
/// </summary>
public sealed class DeviceSessions(ServerConfiguration configuration, PkiStore pki) : IAsyncDisposable
{
    private readonly Dictionary<string, IAsyncDisposable> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ServerConfiguration Configuration { get; } = configuration;

    public bool IsConnected(string device)
    {
        return _clients.GetValueOrDefault(device) switch
        {
            ModbusTcpClient modbus => modbus.IsConnected,
            OpcUaTcpClient opcUa => opcUa.IsConnected && opcUa.IsSessionActive,
            _ => false
        };
    }

    public async ValueTask<DriverResult<ModbusTcpClient>> GetModbusAsync(string device, CancellationToken cancellationToken)
    {
        var settings = Find(device, DeviceConfiguration.ModbusTcp, "read_address or decode_registers");
        if (!settings.IsSuccess)
            return DriverResult<ModbusTcpClient>.Failure(settings.Error);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetOrAdd(device, () =>
            {
                var timeout = TimeSpan.FromMilliseconds(settings.Value.TimeoutMs);
                return new ModbusTcpClient(new ModbusTcpOptions
                {
                    Host = settings.Value.Host,
                    Port = settings.Value.Port,
                    UnitId = settings.Value.UnitId,
                    ConnectTimeout = timeout,
                    RequestTimeout = timeout
                });
            });

            if (!client.IsConnected)
            {
                var connected = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (!connected.IsSuccess)
                    return DriverResult<ModbusTcpClient>.Failure(connected.Error);
            }

            return DriverResult<ModbusTcpClient>.Success(client);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<DriverResult<OpcUaTcpClient>> GetOpcUaAsync(string device, CancellationToken cancellationToken)
    {
        var settings = Find(device, DeviceConfiguration.OpcUa, "read_nodes or browse");
        if (!settings.IsSuccess)
            return DriverResult<OpcUaTcpClient>.Failure(settings.Error);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clients.GetValueOrDefault(device) is not OpcUaTcpClient client)
            {
                var options = await OpcUaSecurity.BuildOptionsAsync(settings.Value, pki, cancellationToken).ConfigureAwait(false);
                if (!options.IsSuccess)
                    return DriverResult<OpcUaTcpClient>.Failure(options.Error);
                client = new OpcUaTcpClient(options.Value);
                _clients[device] = client;
            }

            if (!client.IsConnected)
            {
                var connected = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (!connected.IsSuccess)
                    return await DropAsync(device, client, settings.Value, connected.Error).ConfigureAwait(false);
            }

            if (!client.IsSessionActive)
            {
                var session = await OpenSessionAsync(client, settings.Value, cancellationToken).ConfigureAwait(false);
                if (!session.IsSuccess)
                    return await DropAsync(device, client, settings.Value, session.Error).ConfigureAwait(false);
            }

            return DriverResult<OpcUaTcpClient>.Success(client);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Forgets a secured client whose connection failed, so the next call discovers the endpoint again and
    ///     re-reads trusted/ (an operator may have just approved a certificate on either side).
    /// </summary>
    private async ValueTask<DriverResult<OpcUaTcpClient>> DropAsync(
        string device,
        OpcUaTcpClient client,
        DeviceConfiguration settings,
        DriverError error)
    {
        if (settings.IsUnsecured)
            return DriverResult<OpcUaTcpClient>.Failure(error);

        _clients.Remove(device);
        await client.DisposeAsync().ConfigureAwait(false);
        return DriverResult<OpcUaTcpClient>.Failure(OpcUaSecurity.ExplainRejection(error, pki));
    }

    public static ValueTask<DriverResult> OpenSessionAsync(
        OpcUaTcpClient client,
        DeviceConfiguration settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(settings.Username))
            return client.OpenAnonymousSessionAsync(cancellationToken);

        var password = Environment.GetEnvironmentVariable(settings.PasswordEnv!);
        if (password is null)
        {
            return ValueTask.FromResult(DriverResult.Failure(new DriverError(
                DriverErrorKind.Configuration,
                "PASSWORD_ENV_MISSING",
                $"Environment variable '{settings.PasswordEnv}' is not set for the MCP server process.")));
        }

        return client.OpenUserNameSessionAsync(settings.Username, password, cancellationToken);
    }

    private DriverResult<DeviceConfiguration> Find(string device, string protocol, string tools)
    {
        if (!Configuration.Devices.TryGetValue(device, out var settings))
        {
            return DriverResult<DeviceConfiguration>.Failure(new DriverError(
                DriverErrorKind.Configuration,
                "UNKNOWN_DEVICE",
                $"No device named '{device}'. Call list_devices to see the configured devices."));
        }

        if (!string.Equals(settings.Protocol, protocol, StringComparison.OrdinalIgnoreCase))
        {
            return DriverResult<DeviceConfiguration>.Failure(new DriverError(
                DriverErrorKind.Configuration,
                "WRONG_PROTOCOL",
                $"Device '{device}' speaks {settings.Protocol}; this tool needs {protocol}. Use {tools} only with {protocol} devices."));
        }

        return DriverResult<DeviceConfiguration>.Success(settings);
    }

    private T GetOrAdd<T>(string device, Func<T> create) where T : class, IAsyncDisposable
    {
        if (_clients.TryGetValue(device, out var existing))
            return (T)existing;

        var client = create();
        _clients[device] = client;
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            await client.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
        _gate.Dispose();
    }
}
