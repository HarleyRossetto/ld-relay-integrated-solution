using OpenTelemetry.Metrics;
using OpenTelemetry;
using LDPersistentTelemetry.Context;
using LDPersistentTelemetry.Services;

var builder = WebApplication.CreateBuilder(args);

// Setup logging to be exported via OpenTelemetry
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

var otel = builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("ldrelay.persistence");
        metrics.AddPrometheusExporter();
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource("ldrelay.persistence");
    });

var OtlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

if (OtlpEndpoint != null)
{
    otel.UseOtlpExporter();
}

builder.Services.AddOptions<RedisAutoConfigOptions>()
    .Bind(builder.Configuration.GetSection(RedisAutoConfigOptions.Section));
builder.Services.AddSingleton<IRelayContextProvider, RedisAutoConfigContextProvider>();

builder.AddRedisClient(
    "redis",
    redisSettings => { },
    configOptions =>
    {
        configOptions.AllowAdmin = true;
        configOptions.Ssl = true;
        //     configOptions.CertificateValidation += (_, cert, _, _) =>
        //     {
        //         if (cert is null) return false;
        //         using var chain = new X509Chain();
        //         chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        //         chain.ChainPolicy.CustomTrustStore.Add(valkeyCACert);
        //         chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        //         return chain.Build(new X509Certificate2(cert));
        //     };
    }

);

builder.Services.AddHostedService<RelayProxyMetricScraper>();

var app = builder.Build();

app.UseHttpsRedirection();

app.MapPrometheusScrapingEndpoint();

await app.RunAsync();
