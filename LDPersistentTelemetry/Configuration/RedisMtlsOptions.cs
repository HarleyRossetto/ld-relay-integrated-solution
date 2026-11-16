namespace LDPersistentTelemetry.Configuration;

public sealed class RedisMtlsOptions
{
    public const string SectionName = "Redis:Mtls";

    public string? ClientCertificatePath { get; set; }

    public string? ClientPrivateKeyPath { get; set; }

    public string? CaCertificatePath { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientCertificatePath) &&
        !string.IsNullOrWhiteSpace(ClientPrivateKeyPath) &&
        !string.IsNullOrWhiteSpace(CaCertificatePath);

    public void Validate()
    {
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
