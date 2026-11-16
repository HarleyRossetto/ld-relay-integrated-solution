using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StackExchange.Redis;

namespace LDPersistentTelemetry.Configuration;

public sealed class RedisMtlsConfiguration : IDisposable
{
    private readonly X509Certificate2? _clientCertificate;
    private readonly X509Certificate2? _caCertificate;
    private readonly X509RevocationMode _revocationMode;

    public RedisMtlsConfiguration(RedisMtlsOptions options, string contentRootPath)
    {
        options.Validate();
        _revocationMode = options.RevocationMode;

        if (!options.IsConfigured)
        {
            return;
        }

        X509Certificate2? clientCertificate = null;
        X509Certificate2? caCertificate = null;
        try
        {
            clientCertificate = X509Certificate2.CreateFromPemFile(
                ResolveRequiredPath(options.ClientCertificatePath, "ClientCertificatePath", contentRootPath),
                ResolveRequiredPath(options.ClientPrivateKeyPath, "ClientPrivateKeyPath", contentRootPath));
            caCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(
                ResolveRequiredPath(options.CaCertificatePath, "CaCertificatePath", contentRootPath)));

            if (!caCertificate.Extensions.OfType<X509BasicConstraintsExtension>()
                    .Any(extension => extension.CertificateAuthority))
            {
                throw new InvalidOperationException("The configured Redis CA certificate is not a certificate authority.");
            }

            _clientCertificate = clientCertificate;
            _caCertificate = caCertificate;
            clientCertificate = null;
            caCertificate = null;
        }
        finally
        {
            clientCertificate?.Dispose();
            caCertificate?.Dispose();
        }
    }

    public void Configure(ConfigurationOptions options)
    {
        options.AllowAdmin = true;
        options.Ssl = true;

        if (_clientCertificate is null || _caCertificate is null)
        {
            return;
        }

        options.CertificateSelection += (_, _, _, _, _) => _clientCertificate;
        options.CertificateValidation += (_, certificate, _, sslPolicyErrors) =>
        {
            if (certificate is null ||
                (sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            {
                return false;
            }

            using var serverCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_caCertificate);
            chain.ChainPolicy.RevocationMode = _revocationMode;
            // Require the Redis server certificate to be valid for TLS server authentication.
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(serverCertificate);
        };
    }

    public void Dispose()
    {
        _clientCertificate?.Dispose();
        _caCertificate?.Dispose();
    }

    private static string ResolveRequiredPath(string? configuredPath, string settingName, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException($"Redis mTLS setting '{settingName}' is required.");
        }

        var fullPath = Path.GetFullPath(configuredPath, contentRootPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Configured Redis mTLS file for '{settingName}' was not found.", fullPath);
        }

        return fullPath;
    }
}
