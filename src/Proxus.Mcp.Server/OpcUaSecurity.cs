using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Proxus.Drivers;
using Proxus.Drivers.OpcUa;

namespace Proxus.Mcp;

/// <summary>
///     Turns a device's security settings into driver options. Trust works the way OPC UA operators expect: the
///     server's certificate must be in trusted/ (copied there by a person), and the server must trust ours.
/// </summary>
public static class OpcUaSecurity
{
    private const string ApplicationName = "Proxus Industrial MCP Server";

    // Status codes a server returns when it does not accept this client's certificate.
    private const uint BadSecurityChecksFailed = 0x80130000;
    private const uint BadCertificateUntrusted = 0x801A0000;

    public static async ValueTask<DriverResult<OpcUaTcpOptions>> BuildOptionsAsync(
        DeviceConfiguration settings,
        PkiStore pki,
        CancellationToken cancellationToken)
    {
        if (settings.IsUnsecured)
            return BuildOptions(settings, pki, []);

        var discovered = await DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);
        return discovered.IsSuccess
            ? BuildOptions(settings, pki, discovered.Value)
            : DriverResult<OpcUaTcpOptions>.Failure(discovered.Error);
    }

    /// <summary>Picks the endpoint that matches the device's settings and checks trust; no network traffic.</summary>
    public static DriverResult<OpcUaTcpOptions> BuildOptions(
        DeviceConfiguration settings,
        PkiStore pki,
        OpcUaEndpointDescription[] endpoints)
    {
        var endpoint = new Uri(settings.Endpoint!);
        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);
        var options = new OpcUaTcpOptions
        {
            EndpointUrl = endpoint,
            ConnectTimeout = timeout,
            RequestTimeout = timeout,
            ApplicationName = ApplicationName,
            SessionName = "proxus-mcp"
        };

        if (settings.IsUnsecured)
            return DriverResult<OpcUaTcpOptions>.Success(options with { ApplicationUri = "urn:proxus:mcp" });

        var tokenType = string.IsNullOrEmpty(settings.Username) ? OpcUaUserTokenType.Anonymous : OpcUaUserTokenType.UserName;
        var selected = OpcUaEndpointSelector.Select(endpoints, settings.MessageSecurityMode, settings.SecurityPolicyUri!, tokenType);
        if (!selected.IsSuccess)
            return DriverResult<OpcUaTcpOptions>.Failure(DescribeMissingEndpoint(selected.Error, endpoints, settings, tokenType));

        if (selected.Value.ServerCertificate is not { Length: > 0 } serverCertificateBytes)
        {
            return DriverResult<OpcUaTcpOptions>.Failure(Error(
                "UA_SERVER_CERT_MISSING",
                "The server advertises a secure endpoint without a certificate.",
                "Check the server's certificate configuration."));
        }

        var serverCertificate = X509CertificateLoader.LoadCertificate(serverCertificateBytes);
        if (!pki.IsTrusted(serverCertificate))
        {
            var saved = pki.Reject(serverCertificate);
            return DriverResult<OpcUaTcpOptions>.Failure(Error(
                "UA_SERVER_CERT_UNTRUSTED",
                $"The server certificate is not trusted yet. Subject: {serverCertificate.Subject}. " +
                $"SHA-256 thumbprint: {PkiStore.Sha256Thumbprint(serverCertificate)}. Saved to {saved}.",
                $"Ask the server's administrator to confirm the thumbprint, then move the file into {pki.TrustedDirectory} and retry. " +
                "This server never trusts a certificate on its own."));
        }

        var (ownCertificate, applicationUri) = pki.GetOrCreateApplicationCertificate();
        return DriverResult<OpcUaTcpOptions>.Success(options with
        {
            // Connect to the configured host even when the server advertises another name, a common mismatch in plants.
            EndpointUrl = endpoint,
            SecurityMode = settings.MessageSecurityMode,
            SecurityPolicyUri = settings.SecurityPolicyUri!,
            ApplicationUri = applicationUri,
            ApplicationIdentity = new OpcUaApplicationIdentity(ownCertificate),
            EndpointServerCertificate = serverCertificate,
            CertificateTrust = new OpcUaCertificateTrustOptions
            {
                TrustedPeerCertificates = pki.LoadTrusted(),
                RequireHostName = settings.CheckHostName,
                ExpectedApplicationUri = selected.Value.Server.ApplicationUri
            }
        });
    }

    /// <summary>Adds the next step to a failed secure connection: usually the server has not trusted this client yet.</summary>
    public static DriverError ExplainRejection(DriverError error, PkiStore pki)
    {
        if (error.Code == "UA_CERT_HOSTNAME")
        {
            return error with
            {
                SuggestedAction = "The server certificate does not name the host in the endpoint URL. Use the host name the certificate " +
                                  "contains, or set check_host_name to false for this device if the address is fixed and known."
            };
        }

        var status = error.NativeCode is { } native ? unchecked((uint)native) : 0u;
        if (status is not (BadSecurityChecksFailed or BadCertificateUntrusted) &&
            !error.Message.Contains("BadSecurityChecksFailed", StringComparison.Ordinal) &&
            !error.Message.Contains("BadCertificateUntrusted", StringComparison.Ordinal))
        {
            return error;
        }

        var (certificate, applicationUri) = pki.GetOrCreateApplicationCertificate();
        return OpcUaStatusCodes.Explain(error) with
        {
            Cause = "The server rejected this client's application certificate. OPC UA servers keep new client certificates in a rejected list until an administrator trusts them.",
            SuggestedAction = $"On the OPC UA server, trust the certificate '{ApplicationName}' ({applicationUri}, SHA-256 " +
                              $"{PkiStore.Sha256Thumbprint(certificate)}), usually by moving it from the server's rejected list to trusted. " +
                              $"The file is {pki.OwnCertificatePath}. Then retry."
        };
    }

    public static async ValueTask<DriverResult<OpcUaEndpointDescription[]>> DiscoverAsync(
        DeviceConfiguration settings,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(settings.Endpoint!);
        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);
        // GetEndpoints runs on an unsecured channel by design (OPC 10000-4 5.4.4); it carries no credentials.
        await using var discovery = new OpcUaTcpClient(new OpcUaTcpOptions
        {
            EndpointUrl = endpoint,
            ConnectTimeout = timeout,
            RequestTimeout = timeout
        });
        var connected = await discovery.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!connected.IsSuccess)
            return DriverResult<OpcUaEndpointDescription[]>.Failure(connected.Error);
        return await discovery.GetEndpointsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static DriverError DescribeMissingEndpoint(
        DriverError error,
        OpcUaEndpointDescription[] endpoints,
        DeviceConfiguration settings,
        OpcUaUserTokenType tokenType)
    {
        var offered = endpoints
            .Select(static e => $"{PolicyName(e.SecurityPolicyUri)}/{e.SecurityMode} ({string.Join(", ", e.UserIdentityTokens.Select(static t => t.TokenType))})")
            .Distinct();
        return error with
        {
            Message = $"The server has no endpoint with {settings.SecurityPolicy}, {settings.SecurityMode} and {tokenType} login. " +
                      $"It offers: {string.Join("; ", offered)}.",
            SuggestedAction = "Set security_policy and security_mode to one of the offered combinations, or enable the endpoint on the server."
        };
    }

    public static string PolicyName(string uri)
    {
        var hash = uri.LastIndexOf('#');
        return hash >= 0 ? uri[(hash + 1)..] : uri;
    }

    private static DriverError Error(string code, string message, string action)
    {
        return new DriverError(DriverErrorKind.Configuration, code, message, Protocol: "OPC UA", SuggestedAction: action);
    }
}
