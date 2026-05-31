using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LaunchDarkly.Relay;

/// <summary>
/// Generates ephemeral mTLS certificates for the dev Valkey setup.
/// All certs are signed by a single in-memory CA and are regenerated on every AppHost run.
/// </summary>
public static class MtlsCerts
{
    public record CertPair(string CertPem, string KeyPem);

    /// <summary>Creates a self-signed CA used to sign all other dev certs.</summary>
    public static X509Certificate2 GenerateCA()
    {
        var key = RSA.Create(4096);
        var req = new CertificateRequest(
            "CN=Dev-Valkey-CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
    }

    /// <summary>
    /// Issues a TLS server certificate signed by <paramref name="ca"/>.
    /// SANs cover all hostnames a client might use to reach Valkey:
    /// the Docker short name, the Aspire <c>.dev.internal</c> FQDN, and loopback aliases.
    /// </summary>
    public static X509Certificate2 IssueServerCert(X509Certificate2 ca)
    {
        var key = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=valkey", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false)); // Server Auth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName("valkey");
        san.AddDnsName("valkey.dev.internal");
        san.AddDnsName("redis");
        san.AddDnsName("redis.dev.internal");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());

        var cert = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), RandomSerial());
        return cert.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// Issues a TLS client certificate signed by <paramref name="ca"/>.
    /// The <paramref name="cn"/> becomes the certificate's Common Name and, when
    /// Valkey is configured with <c>tls-auth-clients-user CN</c>, is used as the ACL username.
    /// </summary>
    public static X509Certificate2 IssueClientCert(X509Certificate2 ca, string cn)
    {
        var key = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, critical: false)); // Client Auth

        var cert = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), RandomSerial());
        return cert.CopyWithPrivateKey(key);
    }

    /// <summary>Exports the certificate and its RSA private key as PEM strings.</summary>
    public static CertPair ExportPem(X509Certificate2 cert) =>
        new(cert.ExportCertificatePem(), cert.GetRSAPrivateKey()!.ExportRSAPrivateKeyPem());

    private static byte[] RandomSerial()
    {
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        return serial;
    }
}
