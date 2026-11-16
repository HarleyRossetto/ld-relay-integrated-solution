using System.Security.Cryptography.X509Certificates;

namespace LDPersistentTelemetry.Configuration;

public sealed class RedisMtlsOptions
{
    public const string SectionName = "Redis:Mtls";

    public string? ClientCertificatePath { get; set; }

    public string? ClientPrivateKeyPath { get; set; }

    public string? CaCertificatePath { get; set; }

    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.Online;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientCertificatePath) &&
        !string.IsNullOrWhiteSpace(ClientPrivateKeyPath) &&
        !string.IsNullOrWhiteSpace(CaCertificatePath);

    public void Validate()
    {
        if (!Enum.IsDefined(RevocationMode))
        {
            throw new InvalidOperationException("Redis mTLS RevocationMode must be NoCheck, Online, or Offline.");
        }

        var configuredPaths = new[]
        {
            ClientCertificatePath,
            ClientPrivateKeyPath,
            CaCertificatePath,
        }.Count(path => !string.IsNullOrWhiteSpace(path));

        if (configuredPaths is not 0 and not 3)
        {
            throw new InvalidOperationException(
                "Redis mTLS requires ClientCertificatePath, ClientPrivateKeyPath, and CaCertificatePath to be configured together.");
        }
    }
}
