using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("proxus-mcp-config-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    private ServerConfiguration Load(string json)
    {
        var path = Path.Combine(_directory, "proxus-mcp.json");
        File.WriteAllText(path, json);
        return ServerConfiguration.Load(path);
    }

    [Fact]
    public void Omitted_settings_keep_their_defaults()
    {
        var configuration = Load("""{ "devices": { "m": { "protocol": "modbus-tcp", "host": "10.0.0.1" } } }""");
        var device = configuration.Devices["m"];

        Assert.Equal(502, device.Port);
        Assert.Equal(1, device.UnitId);
        Assert.Equal(3000, device.TimeoutMs);
        Assert.Equal(Path.Combine(_directory, "pki"), configuration.PkiDir);
    }

    [Fact]
    public void Device_names_are_case_insensitive_and_comments_are_allowed()
    {
        var configuration = Load("""
            {
              // comment
              "devices": { "Press-3": { "protocol": "opc-ua", "endpoint": "opc.tcp://h:4840", }, },
            }
            """);

        Assert.True(configuration.Devices.ContainsKey("press-3"));
    }

    [Fact]
    public void Opc_ua_security_settings_map_to_policy_uris()
    {
        var device = Load("""
            { "devices": { "p": { "protocol": "opc-ua", "endpoint": "opc.tcp://h:4840",
                                  "security_mode": "sign_and_encrypt", "security_policy": "Aes256_Sha256_RsaPss" } } }
            """).Devices["p"];

        Assert.False(device.IsUnsecured);
        Assert.Equal("http://opcfoundation.org/UA/SecurityPolicy#Aes256_Sha256_RsaPss", device.SecurityPolicyUri);
        Assert.Contains("Aes256_Sha256_RsaPss", device.DisplayEndpoint);
    }

    [Theory]
    [InlineData("Basic256Sha256", "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256")]
    [InlineData("Aes128_Sha256_RsaOaep", "http://opcfoundation.org/UA/SecurityPolicy#Aes128_Sha256_RsaOaep")]
    [InlineData("aes128_sha256_rsaoaep", "http://opcfoundation.org/UA/SecurityPolicy#Aes128_Sha256_RsaOaep")]
    [InlineData("Aes256_Sha256_RsaPss", "http://opcfoundation.org/UA/SecurityPolicy#Aes256_Sha256_RsaPss")]
    public void Every_supported_security_policy_maps_to_its_uri(string policy, string uri)
    {
        var device = Load($$"""
            { "devices": { "p": { "protocol": "opc-ua", "endpoint": "opc.tcp://h:4840",
                                  "security_mode": "sign", "security_policy": "{{policy}}" } } }
            """).Devices["p"];

        Assert.Equal(uri, device.SecurityPolicyUri);
    }

    [Theory]
    [InlineData("""{ "devices": { "x": { "protocol": "profinet", "host": "h" } } }""", "not supported")]
    [InlineData("""{ "devices": { "x": { "protocol": "modbus-tcp" } } }""", "host is required")]
    [InlineData("""{ "devices": { "x": { "protocol": "opc-ua", "endpoint": "http://h" } } }""", "opc.tcp://")]
    [InlineData("""{ "devices": { "x": { "protocol": "opc-ua", "endpoint": "opc.tcp://h:1", "username": "u" } } }""", "password_env")]
    [InlineData("""{ "devices": { "x": { "protocol": "opc-ua", "endpoint": "opc.tcp://h:1", "security_mode": "encrypt" } } }""", "security_mode")]
    [InlineData("""{ "devices": { "x": { "protocol": "opc-ua", "endpoint": "opc.tcp://h:1", "security_mode": "sign", "security_policy": "Basic128Rsa15" } } }""", "security_policy")]
    [InlineData("""{ "devices": { "x": { "protocol": "modbus-tcp", "host": "h", "timeout_ms": 5 } } }""", "timeout_ms")]
    public void Invalid_devices_are_rejected_with_the_setting_named(string json, string expected)
    {
        var exception = Assert.Throws<InvalidDataException>(() => Load(json));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Missing_file_is_reported_with_its_full_path()
    {
        var exception = Assert.Throws<FileNotFoundException>(() => ServerConfiguration.Load(Path.Combine(_directory, "none.json")));
        Assert.Contains(_directory, exception.Message);
    }
}
