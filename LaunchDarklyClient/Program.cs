using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using LaunchDarkly.Logging;
using LaunchDarkly.Observability;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Integrations;
using LaunchDarkly.Sdk.Server.Interfaces;
using OpenTelemetry;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Setup logging to be exported via OpenTelemetry
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

builder.Configuration.AddEnvironmentVariables();

// TODO: See about removing this and injecting into Options formatted config and avoid this atypical setup
// Load mTLS certificates injected by the AppHost.
// ldclient authenticates to Valkey via its cert CN - no password needed.
static string? FromBase64Env(IConfiguration cfg, string key)
{
    var b64 = cfg[key];
    return string.IsNullOrEmpty(b64) ? null : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
}

var clientCertPem = FromBase64Env(builder.Configuration, "VALKEY_CLIENT_CERT_B64");
var clientKeyPem = FromBase64Env(builder.Configuration, "VALKEY_CLIENT_KEY_B64");

X509Certificate2? valkeyClientCert = null;

if (clientCertPem is not null && clientKeyPem is not null)
    valkeyClientCert = X509Certificate2.CreateFromPem(clientCertPem, clientKeyPem);

string applicationName = builder.Configuration["APPLICATION_NAME"] ?? "MyAspireApp";
string applicationVersion = builder.Configuration["APPLICATION_VERSION"] ??
    Assembly.GetExecutingAssembly().GetName().Version?.ToString() ??
    "dev";

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
                .Events("https://events.launchdarkly.com")
            )
            .Plugins(new PluginConfigurationBuilder()
                .Add(ObservabilityPlugin.ForExistingServices())
            )
            .Logging(Logs.CoreLogging(sp.GetRequiredService<ILoggerFactory>()))
            .BigSegments(Components.BigSegments(
                Redis.BigSegmentStore()
                    .Prefix(builder.Configuration["REDIS_KEY_PREFIX"])
                    // TODO: Consider removing this and providing a prepared Redis client
                    .RedisConfiguration(BuildValkeyConfig(
                        builder.Configuration,
                        valkeyClientCert))
                )
            )
            .Build();

        return new LdClient(launchdarklyClientConfig);
    });

builder.Services.AddHostedService<BackgroundFlagPollService>();

// var otel = builder.Services.AddOpenTelemetry();
//
// // Export OpenTelemetry data via OTLP, using env vars for the configuration
// var OtlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
//
// if (OtlpEndpoint != null)
// {
//     otel.UseOtlpExporter();
// }

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();

static ConfigurationOptions BuildValkeyConfig(IConfiguration cfg, X509Certificate2? client) //, X509Certificate2? caCert)
{
    var options = new ConfigurationOptions
    {
        EndPoints = { $"{cfg["REDIS_HOST"]}:{cfg["REDIS_PORT"]}" },
        SslHost = cfg["REDIS_HOST"],
        Ssl = true,
        AbortOnConnectFail = false,
    };

    if (client is not null)
        options.CertificateSelection += delegate { return client; };

    // CA trust is handled by Aspire's WithCertificateAuthorityCollection.
    // Use System trust with revocation disabled — the ephemeral CA has no CRL endpoint.
    options.CertificateValidation += (_, cert, _, _) =>
    {
        if (cert is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.System;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(cert));
    };

    return options;
}

file class BackgroundFlagPollService(ILdClient ldClient, ILogger<BackgroundFlagPollService> logger) : BackgroundService
{
    private const string FlagKey = "TestFlag";
    private readonly ILdClient _ldClient = ldClient;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var context = Context.Builder("Harley").Build();
        while (!stoppingToken.IsCancellationRequested)
        {
            var variationDetail = _ldClient.BoolVariationDetail(FlagKey, context, false);
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
                logger.LogInformation("Flag Key: {Key}, Flag value: {Value}, Reason: {Reason}",
                                      FlagKey,
                                      variationDetail.Value,
                                      variationDetail.Reason);
            if (variationDetail.Reason.Kind == EvaluationReasonKind.Error)
            {
                logger.LogError("Error evaluating flag: {ErrorKind}", variationDetail.Reason.ErrorKind);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
