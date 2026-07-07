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

    /// <summary>
    /// Creates a self-signed RSA certificate authority used to sign all other dev certs.
    /// </summary>
    /// <param name="subjectName">The full X.500 distinguished name for the CA (e.g. <c>"CN=Dev-CA"</c>).</param>
    /// <param name="keyBitsSize">RSA key size in bits. Defaults to 4096.</param>
    /// <param name="notBefore">Certificate validity start. Defaults to one day before now.</param>
    /// <param name="notAfter">Certificate validity end. Defaults to one year from now.</param>
    /// <returns>A self-signed CA certificate with <c>KeyCertSign</c> and <c>CrlSign</c> key usages set.</returns>
    public static X509Certificate2 GenerateCertificateAuthority(string subjectName, int keyBitsSize = 4096, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        notBefore ??= DateTimeOffset.UtcNow.AddDays(-1);
        notAfter ??= DateTimeOffset.UtcNow.AddYears(1);

        var key = RSA.Create(keyBitsSize);
        var req = new CertificateRequest($"CN={subjectName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, false, 0, true));

        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

        return req.CreateSelfSigned(notBefore.Value, notAfter.Value);
    }

    /// <summary>
    /// Issues a TLS server certificate signed by <paramref name="certificateAuthority"/>.
    /// </summary>
    /// <param name="certificateAuthority">The CA certificate used to sign this server certificate.</param>
    /// <param name="commonName">The Common Name for the certificate subject.</param>
    /// <param name="sanDnsNames">DNS names to include in the Subject Alternative Name extension.</param>
    /// <param name="sanIpAddresses">Optional IP addresses to include in the Subject Alternative Name extension.</param>
    /// <param name="keyBitSize">RSA key size in bits. Defaults to 4096.</param>
    /// <param name="notBefore">Certificate validity start. Defaults to one day before now.</param>
    /// <param name="notAfter">Certificate validity end. Defaults to one year from now, clamped to the CA's expiry.</param>
    /// <returns>A CA-signed server certificate with its RSA private key attached.</returns>
    public static X509Certificate2 IssueServerCert(this X509Certificate2 certificateAuthority,
                                                   string commonName,
                                                   string[] sanDnsNames,
                                                   System.Net.IPAddress[]? sanIpAddresses = null,
                                                   int keyBitSize = 4096,
                                                   DateTimeOffset? notBefore = null,
                                                   DateTimeOffset? notAfter = null)
    {
        notBefore ??= DateTimeOffset.UtcNow.AddDays(-1);
        notAfter = notAfter.HasValue && notAfter.Value < certificateAuthority.NotAfter
            ? notAfter.Value
            : certificateAuthority.NotAfter;

        var key = RSA.Create(keyBitSize);

        var req = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);


        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // Server Auth

        var san = new SubjectAlternativeNameBuilder();
        foreach (var dnsName in sanDnsNames)
        {
            san.AddDnsName(dnsName);
        }

        if (sanIpAddresses is not null)
        {
            foreach (var ipAddress in sanIpAddresses)
            {
                san.AddIpAddress(ipAddress);
            }
        }

        req.CertificateExtensions.Add(san.Build());

        var cert = req.Create(certificateAuthority, notBefore.Value, notAfter.Value, RandomSerial());
        return cert.CopyWithPrivateKey(key);
    }


    /// <summary>
    /// Issues a TLS client certificate signed by <paramref name="certificateAuthority"/>.
    /// The <paramref name="commonName"/> becomes the certificate's Common Name and, when
    /// Valkey is configured with <c>tls-auth-clients-user CN</c>, is used as the ACL username.
    /// </summary>
    /// <param name="certificateAuthority">The CA certificate used to sign this client certificate.</param>
    /// <param name="commonName">The Common Name for the certificate subject, used as the ACL username.</param>
    /// <param name="keySizeInBits">RSA key size in bits. Defaults to 4096.</param>
    /// <param name="notBefore">Certificate validity start. Defaults to one day before now.</param>
    /// <param name="notAfter">Certificate validity end. Defaults to one year from now, clamped to the CA's expiry.</param>
    /// <returns>A CA-signed client certificate with its RSA private key attached.</returns>
    public static X509Certificate2 IssueClientCert(this X509Certificate2 certificateAuthority,
                                                   string commonName,
                                                   int keySizeInBits = 4096,
                                                   DateTimeOffset? notBefore = null,
                                                   DateTimeOffset? notAfter = null)
    {

        notBefore ??= DateTimeOffset.UtcNow.AddDays(-1);
        notAfter = notAfter.HasValue && notAfter.Value < certificateAuthority.NotAfter
            ? notAfter.Value
            : certificateAuthority.NotAfter;

        var key = RSA.Create(keySizeInBits);
        var req = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.2")], critical: false)); // Client Auth

        var cert = req.Create(certificateAuthority, notBefore.Value, notAfter.Value, RandomSerial());
        return cert.CopyWithPrivateKey(key);
    }

    public static string ExportPrivateKeyPem(this X509Certificate2 certificate)
    {
        return certificate.GetRSAPrivateKey()!.ExportRSAPrivateKeyPem();
    }

    /// <summary>Exports the certificate and its RSA private key as PEM strings.</summary>
    /// <param name="cert">The certificate to export. Must have an RSA private key attached.</param>
    /// <returns>A <see cref="CertPair"/> containing the PEM-encoded certificate and private key.</returns>
    public static CertPair ExportPem(this X509Certificate2 cert) =>
        new(cert.ExportCertificatePem(), cert.GetRSAPrivateKey()!.ExportRSAPrivateKeyPem());

    /// <summary>Generates a cryptographically random 16-byte certificate serial number.</summary>
    /// <returns>A 16-byte array suitable for use as an X.509 serial number.</returns>
    private static byte[] RandomSerial()
    {
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        return serial;
    }
}
