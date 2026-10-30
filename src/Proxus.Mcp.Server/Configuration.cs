using System.Text.Json;
using System.Text.Json.Serialization;
using Proxus.Drivers.OpcUa;

namespace Proxus.Mcp;

/// <summary>Devices the server may talk to. Nothing outside this file is reachable.</summary>
/// <remarks>Setters, not init: the JSON source generator sets every init-only property and would replace the defaults.</remarks>
public sealed class ServerConfiguration
{
    // Populated in place so device names keep their case-insensitive lookup.
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, DeviceConfiguration> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>OPC UA certificate folders (own, trusted, rejected). Relative paths start at the configuration file.</summary>
    public string PkiDir { get; set; } = "pki";

    public static ServerConfiguration Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration file not found: {Path.GetFullPath(path)}", path);

        using var stream = File.OpenRead(path);
        var configuration = JsonSerializer.Deserialize(stream, ConfigurationJsonContext.Default.ServerConfiguration)
                            ?? throw new InvalidDataException($"Configuration file is empty: {path}");

        foreach (var (name, device) in configuration.Devices)
            device.Validate(name);

        configuration.PkiDir = Path.GetFullPath(configuration.PkiDir, Path.GetDirectoryName(Path.GetFullPath(path))!);

        return configuration;
    }
}

public sealed class DeviceConfiguration
{
    public const string ModbusTcp = "modbus-tcp";
    public const string OpcUa = "opc-ua";

    public string Protocol { get; set; } = ModbusTcp;

    /// <summary>ISA-95 path such as Acme/Gebze/Press/Line3, the same naming a Unified Namespace uses.</summary>
    public string? Path { get; set; }

    /// <summary>Request timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 3000;

    // Modbus TCP
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;

    // OPC UA
    /// <summary>opc.tcp:// endpoint URL. The connection always goes to this host and port, even when the server
    /// advertises another host name in its endpoint descriptions.</summary>
    public string? Endpoint { get; set; }

    /// <summary>none, sign or sign_and_encrypt.</summary>
    public string SecurityMode { get; set; } = "none";

    /// <summary>Basic256Sha256 or Aes256_Sha256_RsaPss; used when security_mode is not none.</summary>
    public string SecurityPolicy { get; set; } = "Basic256Sha256";

    /// <summary>Require the server certificate to name the endpoint host. Turn off only for servers whose certificate
    /// lacks the IP address or host name used to reach them.</summary>
    public bool CheckHostName { get; set; } = true;

    /// <summary>User name for a user-name session; anonymous when empty.</summary>
    public string? Username { get; set; }

    /// <summary>Name of the environment variable that holds the password, so the password never sits in this file.</summary>
    public string? PasswordEnv { get; set; }

    public bool IsModbus => string.Equals(Protocol, ModbusTcp, StringComparison.OrdinalIgnoreCase);
    public bool IsOpcUa => string.Equals(Protocol, OpcUa, StringComparison.OrdinalIgnoreCase);

    public bool IsUnsecured => string.Equals(SecurityMode, "none", StringComparison.OrdinalIgnoreCase);

    public OpcUaMessageSecurityMode MessageSecurityMode => SecurityMode.ToLowerInvariant() switch
    {
        "sign" => OpcUaMessageSecurityMode.Sign,
        "sign_and_encrypt" => OpcUaMessageSecurityMode.SignAndEncrypt,
        _ => OpcUaMessageSecurityMode.None
    };

    public string? SecurityPolicyUri => SecurityPolicy.ToLowerInvariant() switch
    {
        // SecurityPolicy URIs from OPC 10000-7; the driver keeps its own constants internal.
        "basic256sha256" => "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256",
        "aes256_sha256_rsapss" => "http://opcfoundation.org/UA/SecurityPolicy#Aes256_Sha256_RsaPss",
        _ => null
    };

    public string DisplayEndpoint => IsOpcUa
        ? IsUnsecured ? $"{Endpoint} (unsecured)" : $"{Endpoint} ({SecurityPolicy}, {SecurityMode})"
        : $"{Host}:{Port} unit {UnitId}";

    internal void Validate(string name)
    {
        if (TimeoutMs is < 100 or > 60_000)
            throw new InvalidDataException($"Device '{name}': timeout_ms must be between 100 and 60000.");

        if (IsModbus)
        {
            if (string.IsNullOrWhiteSpace(Host))
                throw new InvalidDataException($"Device '{name}': host is required.");
            return;
        }

        if (IsOpcUa)
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "opc.tcp")
                throw new InvalidDataException($"Device '{name}': endpoint must be an opc.tcp:// URL.");
            if (!string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(PasswordEnv))
                throw new InvalidDataException($"Device '{name}': password_env is required with username.");
            if (SecurityMode.ToLowerInvariant() is not ("none" or "sign" or "sign_and_encrypt"))
                throw new InvalidDataException($"Device '{name}': security_mode must be none, sign or sign_and_encrypt.");
            if (!IsUnsecured && SecurityPolicyUri is null)
                throw new InvalidDataException($"Device '{name}': security_policy must be Basic256Sha256 or Aes256_Sha256_RsaPss.");
            return;
        }

        throw new InvalidDataException($"Device '{name}': protocol '{Protocol}' is not supported. Use '{ModbusTcp}' or '{OpcUa}'.");
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ServerConfiguration))]
internal sealed partial class ConfigurationJsonContext : JsonSerializerContext;
