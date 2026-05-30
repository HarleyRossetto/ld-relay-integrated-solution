using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using LaunchDarkly.Logging;
using LaunchDarkly.Observability;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Integrations;
using LaunchDarkly.Sdk.Server.Interfaces;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

// Load mTLS certificates injected by the AppHost.
// ldclient authenticates to Valkey via its cert CN — no password needed.
static string? FromBase64Env(IConfiguration cfg, string key)
{
    var b64 = cfg[key];
    return string.IsNullOrEmpty(b64) ? null : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
}

var caCertPem      = FromBase64Env(builder.Configuration, "VALKEY_CA_CERT_B64");
var clientCertPem  = FromBase64Env(builder.Configuration, "VALKEY_CLIENT_CERT_B64");
var clientKeyPem   = FromBase64Env(builder.Configuration, "VALKEY_CLIENT_KEY_B64");

X509Certificate2? valkeyClientCert = null;
X509Certificate2? valkeyCACert     = null;

if (clientCertPem is not null && clientKeyPem is not null)
    valkeyClientCert = X509Certificate2.CreateFromPem(clientCertPem, clientKeyPem);

if (caCertPem is not null)
    valkeyCACert = X509Certificate2.CreateFromPem(caCertPem);


string applicationName = builder.Configuration["APPLICATION_NAME"] ?? "MyAspireApp";
string applicationVersion = builder.Configuration["APPLICATION_VERSION"] ?? "1.0.0";

builder.Services.AddLaunchDarklyObservability(builder.Configuration["LAUNCHDARKLY_SDK_KEY"], cfg =>
        {
            cfg.WithServiceName(applicationName)
                .WithServiceVersion(applicationVersion)
                .WithEnvironment(builder.Environment.EnvironmentName)
                .WithOtlpEndpoint(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        });

builder.Services.AddSingleton<ILdClient>(sp =>
    {
        var launchdarklyClientConfig = Configuration.Builder(builder.Configuration["LAUNCHDARKLY_SDK_KEY"])
            .ApplicationInfo(Components.ApplicationInfo()
                .ApplicationId(applicationName)
                .ApplicationName(applicationName)
                .ApplicationVersion(applicationVersion)
                .ApplicationVersionName(applicationVersion)
            )
            // .Logging(Logs.CoreLogging(new Logg
            .ServiceEndpoints(Components.ServiceEndpoints()
                .RelayProxy(builder.Configuration["GATEWAY_HTTPS"])
            )
            .Plugins(new PluginConfigurationBuilder()
                .Add(ObservabilityPlugin.ForExistingServices())
            )
            .Logging(Logs.CoreLogging(sp.GetRequiredService<ILoggerFactory>()))
            .BigSegments(Components.BigSegments(
                Redis.BigSegmentStore()
                    .Prefix(builder.Configuration["REDIS_KEY_PREFIX"])
                    .RedisConfiguration(BuildValkeyConfig(
                        builder.Configuration,
                        valkeyClientCert,
                        valkeyCACert))
                )
            )
            .Build();

        return new LdClient(launchdarklyClientConfig);
    });

builder.Services.AddHostedService<BackgroundFlagPollService>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();

/// <summary>
/// Builds a StackExchange.Redis <see cref="ConfigurationOptions"/> with mTLS support.
/// When a client cert is provided, it is presented during the TLS handshake so Valkey can
/// identify the connection by cert CN (via <c>tls-auth-clients-user CN</c>) — no password needed.
/// </summary>
static ConfigurationOptions BuildValkeyConfig(
    IConfiguration cfg,
    X509Certificate2? clientCert,
    X509Certificate2? caCert)
{
    var options = new ConfigurationOptions
    {
        // Use REDIS_HOST:REDIS_PORT from Aspire's injected individual variables.
        // Do NOT use REDIS_URI (a full URI like rediss://...@host:port) in EndPoints —
        // StackExchange.Redis treats it as a hostname and appends its own default SSL
        // port (6380), resulting in an unreachable host:port:6380 endpoint.
        EndPoints = { $"{cfg["REDIS_HOST"]}:{cfg["REDIS_PORT"]}" },
        Ssl = true,
        AbortOnConnectFail = false,
        // Password left null/empty: ldclient is authenticated by cert CN (nopass ACL user).
    };

    if (clientCert is not null)
        options.CertificateSelection += (_, _, _, _, _) => clientCert;

    if (caCert is not null)
    {
        options.CertificateValidation += (_, cert, _, _) =>
        {
            if (cert is null) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(caCert);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(cert));
        };
    }

    return options;
}

file class BackgroundFlagPollService(ILdClient ldClient, ILogger<BackgroundFlagPollService> logger) : BackgroundService
{
    private readonly ILdClient _ldClient = ldClient;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var context = Context.Builder("user-key").Build();
        while (!stoppingToken.IsCancellationRequested)
        {
            var variationDetail = _ldClient.BoolVariationDetail("TestFlag", context, false);
            if (variationDetail.Reason.Kind == EvaluationReasonKind.Error)
            {
                logger.LogError("Error evaluating flag: {ErrorKind}", variationDetail.Reason.ErrorKind);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
