using LDPersistentTelemetry.Configuration;
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
builder.Services.AddOptions<PersistentStoreTelemetryOptions>()
    .Bind(builder.Configuration.GetSection(PersistentStoreTelemetryOptions.Section))
    .Validate(options => options.ScrapeInterval > TimeSpan.Zero, "ScrapeInterval must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<IRelayContextProvider, RedisAutoConfigContextProvider>();

var redisMtlsOptions = builder.Configuration
    .GetSection(RedisMtlsOptions.SectionName)
    .Get<RedisMtlsOptions>() ?? new RedisMtlsOptions();
using var redisMtlsConfiguration = new RedisMtlsConfiguration(
    redisMtlsOptions,
    builder.Environment.ContentRootPath);

builder.AddRedisClient(
    "redis",
    redisSettings => { },
    redisMtlsConfiguration.Configure);

builder.Services.AddHostedService<RelayProxyMetricScraper>();

var app = builder.Build();

app.UseHttpsRedirection();

app.MapPrometheusScrapingEndpoint();

await app.RunAsync();
