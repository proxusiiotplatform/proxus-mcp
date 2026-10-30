using System.Security.Cryptography.X509Certificates;
using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

public sealed class PkiStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("proxus-mcp-pki-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Application_certificate_is_created_once_and_reloaded_unchanged()
    {
        var (created, uri) = new PkiStore(_root).GetOrCreateApplicationCertificate();
        var (reloaded, reloadedUri) = new PkiStore(_root).GetOrCreateApplicationCertificate();

        Assert.True(created.HasPrivateKey);
        Assert.True(reloaded.HasPrivateKey);
        Assert.Equal(created.Thumbprint, reloaded.Thumbprint);
        Assert.Equal(uri, reloadedUri);
        Assert.StartsWith("urn:", uri);
    }

    [Fact]
    public void Application_certificate_names_its_uri_and_allows_client_authentication()
    {
        var (certificate, uri) = new PkiStore(_root).GetOrCreateApplicationCertificate();

        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains(uri, names.Format(false));
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages;
        Assert.Contains(usages.Cast<System.Security.Cryptography.Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.2");
        Assert.True(certificate.GetRSAPublicKey()!.KeySize >= 2048);
    }

    [Fact]
    public void Private_key_is_readable_only_by_the_owner()
    {
        if (OperatingSystem.IsWindows())
            return;

        new PkiStore(_root).GetOrCreateApplicationCertificate();
        var mode = File.GetUnixFileMode(Path.Combine(_root, "own", "private", "key.pem"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Rejected_certificate_becomes_trusted_only_after_it_is_moved()
    {
        var store = new PkiStore(_root);
        using var server = X509CertificateLoader.LoadCertificate(new PkiStore(Path.Combine(_root, "other")).GetOrCreateApplicationCertificate().Certificate.RawData);

        Assert.False(store.IsTrusted(server));
        var saved = store.Reject(server);
        Assert.True(File.Exists(saved));
        Assert.False(store.IsTrusted(server));

        Directory.CreateDirectory(store.TrustedDirectory);
        File.Move(saved, Path.Combine(store.TrustedDirectory, Path.GetFileName(saved)));
        Assert.True(store.IsTrusted(server));
    }

    [Fact]
    public void Unreadable_files_in_trusted_are_ignored()
    {
        var store = new PkiStore(_root);
        Directory.CreateDirectory(store.TrustedDirectory);
        File.WriteAllText(Path.Combine(store.TrustedDirectory, "broken.der"), "not a certificate");
        File.WriteAllText(Path.Combine(store.TrustedDirectory, "notes.txt"), "ignored");

        Assert.Empty(store.LoadTrusted());
    }
}
