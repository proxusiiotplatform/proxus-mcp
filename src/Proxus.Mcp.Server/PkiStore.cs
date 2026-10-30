using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Proxus.Mcp;

/// <summary>
///     The server's OPC UA certificate folders, in the layout UaExpert and most OPC UA tools use:
///     <c>own/</c> holds this application's certificate and key, <c>trusted/</c> the server certificates an
///     operator has approved, <c>rejected/</c> the ones seen but not approved. Nothing is ever trusted automatically.
/// </summary>
public sealed class PkiStore(string root)
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    private readonly Lock _ownGate = new();
    private (X509Certificate2 Certificate, string ApplicationUri)? _own;

    public string Root { get; } = Path.GetFullPath(root);
    public string OwnCertificatePath => Path.Combine(Root, "own", "cert.der");
    private string OwnKeyPath => Path.Combine(Root, "own", "private", "key.pem");
    private string OwnUriPath => Path.Combine(Root, "own", "application-uri.txt");
    public string TrustedDirectory => Path.Combine(Root, "trusted");
    public string RejectedDirectory => Path.Combine(Root, "rejected");

    /// <summary>Loads this application's certificate, creating a self-signed one on first use.</summary>
    public (X509Certificate2 Certificate, string ApplicationUri) GetOrCreateApplicationCertificate()
    {
        lock (_ownGate)
        {
            return _own ??= File.Exists(OwnCertificatePath) ? LoadOwn() : CreateOwn();
        }
    }

    public X509Certificate2[] LoadTrusted()
    {
        if (!Directory.Exists(TrustedDirectory))
            return [];

        var certificates = new List<X509Certificate2>();
        foreach (var file in Directory.EnumerateFiles(TrustedDirectory))
        {
            if (Path.GetExtension(file).ToLowerInvariant() is not (".der" or ".cer" or ".crt" or ".pem"))
                continue;
            try
            {
                certificates.Add(X509CertificateLoader.LoadCertificateFromFile(file));
            }
            catch (CryptographicException)
            {
                // An unreadable file in trusted/ is not trust; skip it rather than fail every connection.
            }
        }

        return certificates.ToArray();
    }

    public bool IsTrusted(X509Certificate2 certificate)
    {
        return LoadTrusted().Any(trusted => trusted.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span));
    }

    /// <summary>Saves an unapproved server certificate so an operator can inspect it and move it to trusted/.</summary>
    public string Reject(X509Certificate2 certificate)
    {
        Directory.CreateDirectory(RejectedDirectory);
        var path = Path.Combine(RejectedDirectory, $"{FileSafe(certificate)}.der");
        File.WriteAllBytes(path, certificate.RawData);
        return path;
    }

    public static string Sha256Thumbprint(X509Certificate2 certificate)
    {
        return Convert.ToHexString(SHA256.HashData(certificate.RawDataMemory.Span));
    }

    private (X509Certificate2, string) LoadOwn()
    {
        using var publicCertificate = X509CertificateLoader.LoadCertificateFromFile(OwnCertificatePath);
        using var key = RSA.Create();
        key.ImportFromPem(File.ReadAllText(OwnKeyPath));
        var applicationUri = File.ReadAllText(OwnUriPath).Trim();
        return (publicCertificate.CopyWithPrivateKey(key), applicationUri);
    }

    private (X509Certificate2, string) CreateOwn()
    {
        var host = Dns.GetHostName();
        var applicationUri = $"urn:{host}:proxus:mcp";
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=Proxus Industrial MCP Server, O=Proxus, DC={host}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation |
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(ClientAuthenticationOid), new Oid(ServerAuthenticationOid) },
            false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddUri(new Uri(applicationUri));
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        Directory.CreateDirectory(Path.GetDirectoryName(OwnKeyPath)!);
        File.WriteAllBytes(OwnCertificatePath, created.RawData);
        File.WriteAllText(OwnUriPath, applicationUri);
        WritePrivate(OwnKeyPath, key.ExportPkcs8PrivateKeyPem());

        // CreateSelfSigned already attaches the key; reload the public part so both paths return the same shape.
        using var publicCertificate = X509CertificateLoader.LoadCertificate(created.RawData);
        return (publicCertificate.CopyWithPrivateKey(key), applicationUri);
    }

    private static void WritePrivate(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }

        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static string FileSafe(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        var safe = new string(name.Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return $"{safe} [{Sha256Thumbprint(certificate)[..16]}]";
    }
}
