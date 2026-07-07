using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LaunchDarkly.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var launchdarklySdkKeyParameter = builder.AddParameter(name: "launchdarkly-sdk-key", secret: true);
var launchdarklyRelayAutoConfigKeyParameter = builder.AddParameter(name: "launchdarkly-relay-auto-config", secret: true);
var launchdarklyEnvironmentIdParameter = builder.AddParameter(name: "launchdarkly-environment");

// Generate ephemeral mTLS certs and ACL credentials for Valkey.
// All certs are CA-signed and regenerated on every run.
var mtlsCertificateAuthority = MtlsCerts.GenerateCertificateAuthority("valkey-ca");
var redisCert = mtlsCertificateAuthority.IssueServerCert("redis", ["redis", "localhost", "redis.dev.internal"], [System.Net.IPAddress.Loopback]);
// var ldclientCert = MtlsCerts.IssueClientCert(mtlsCertificateAuthority, "ldclient.commonname");
var ldClientCert = mtlsCertificateAuthority.IssueClientCert("ldclient.commonname");
var insightsCert = mtlsCertificateAuthority.IssueClientCert("redisinsight.commonname");

// Build the CA trust collection for Aspire's certificate distribution API.
var customMtlsCaBundle = builder.AddCertificateAuthorityCollection("valkey-ca")
    .WithCertificates(new X509Certificate2Collection(mtlsCertificateAuthority));

// LaunchDarkly External Service
var externalLaunchDarklyStream = builder.AddExternalService("launchdarkly-stream", new Uri("https://stream.launchdarkly.com"));
var externalLaunchDarklyEvents = builder.AddExternalService("launchdarkly-events", new Uri("https://events.launchdarkly.com"));

// TODO: Review Garnet and verify performance vs Valkey/Redis
//       https://github.com/microsoft/garnet
// var cache = builder.AddGarnet("cache");

// Random password for the relay ACL user (relay connects without a client cert).
var relayPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

var valkey = builder.AddValkey("redis")
    .WithDataVolume()
    .WithPersistence(interval: TimeSpan.FromMinutes(5), keysChangedThreshold: 100)
    .WithContainerFiles("/certs",
        async (context, cancellationToken) =>
        {
            return
            [
                new ContainerFile { Name = "ca.crt",     Contents = mtlsCertificateAuthority.ExportCertificatePem() },
                new ContainerFile { Name = "server.crt", Contents = redisCert.ExportCertificatePem() },
                new ContainerFile { Name = "server.key", Contents = redisCert.ExportPrivateKeyPem() },
            ];
        })
    .WithContainerFiles("/etc/valkey",
        async (context, cancellationToken) =>
        {
            // Resolve the actual parameter value — interpolating the resource builder directly
            // just produces the type name, which breaks ACL parsing.
            var envId = await launchdarklyEnvironmentIdParameter.Resource.GetValueAsync(cancellationToken);
            return
            [
                new ContainerFile
                {
                    Name = "valkey.conf",
                    Contents =
                        """
                        loglevel debug
                        # Disable plaintext port; serve TLS only on 6379
                        port 0
                        tls-port 6379
                        tls-replication no

                        # Close the connection after client has been idle for N seconds. 0 to disable.
                        timeout 0

                        # User data, including keys, values, client names, and ACL usernames, can be
                        # logged as part of assertions and other error cases. To prevent sensitive user
                        # information, such as PII, from being recorded in the server log file, this
                        # user data is hidden from the log by default. If you need to log user data for
                        # debugging or troubleshooting purposes, you can disable this feature by
                        # changing the config value to no.
                        # With this set to yes Valkey will log messages like: TLS: Auto-authenticated client as *redacted*
                        hide-user-data-from-log no

                        # Client certs are optional: relay connects with password only,
                        # client apps connect with a cert whose CN is their ACL username.
                        tls-auth-clients optional
                        tls-auth-clients-user CN

                        tls-cert-file /certs/server.crt
                        tls-key-file  /certs/server.key
                        tls-ca-cert-file /certs/ca.crt

                        aclfile /etc/valkey/users.acl
                        """
                },
                new ContainerFile
                {
                    Name = "users.acl",
                        // user default off nopass nocommands resetkeys resetchannels
                    Contents =
                        $"""
                        user relay on >{relayPassword} ~* +@all
                        user ldclient.commonname on nopass ~{envId}* +@all
                        user redisinsight.commonname on nopass ~* +@all
                        """
                },
            ];
        });
// .WithValkeyAdmin();

builder.AddProject<LDPersistentTelemetry>("ld-persistent-telemetry-sidecar")
    .WithCertificateAuthorityCollection(customMtlsCaBundle)
    .WaitFor(valkey)
    .WithReference(valkey)
    .WithEnvironment("REDIS_USER", "relay")
    .WithEnvironment("RelayProxyAutoConfigCache__AutoConfigCacheKey", "AutoConfigCacheKey")
    .WithEnvironment("RelayProxyAutoConfigCache__EncryptionKey", launchdarklyRelayAutoConfigKeyParameter)
    .WithOtlpExporter();

// Aspire's AddValkey generates: /bin/sh -c "valkey-server --requirepass $VALKEY_PASSWORD"
// Appending WithArgs would add our config path as $0 to the shell (a no-op for valkey).
// Instead, remove Aspire's built-in args callback and replace it with one that embeds
// the config file path inside the valkey-server command string.
foreach (var ann in valkey.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().ToList())
    valkey.Resource.Annotations.Remove(ann);

valkey.WithArgs(context =>
{
    context.Args.Add("-c");
    context.Args.Add("valkey-server /etc/valkey/valkey.conf --requirepass \"$VALKEY_PASSWORD\"");
    return Task.CompletedTask;
});

// Aspire's built-in Valkey health check uses the plain connection string (no ssl=true) and
// doesn't know about our custom CA, so replace it with a TLS-aware implementation using
// AspNetCore.HealthChecks.Redis's IConnectionMultiplexer factory overload.
foreach (var ann in valkey.Resource.Annotations.OfType<HealthCheckAnnotation>().ToList())
    valkey.Resource.Annotations.Remove(ann);

const string valkeyHealthCheckKey = "valkey-tls";
builder.Services.AddHealthChecks().AddRedis(
    connectionMultiplexerFactory: _ =>
    {
        var ep = valkey.Resource.PrimaryEndpoint.EndpointAnnotation.AllocatedEndpoint
            ?? throw new InvalidOperationException("Valkey endpoint not yet allocated");

        var opts = new ConfigurationOptions
        {
            EndPoints = { { ep.Address ?? "127.0.0.1", ep.Port } },
            Ssl = true,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            User = "relay",
            Password = relayPassword,
            AbortOnConnectFail = false,
        };
        opts.CertificateValidation += (_, cert, _, _) =>
        {
            if (cert is null) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(mtlsCertificateAuthority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(
                X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)));
        };
        return ConnectionMultiplexer.Connect(opts);
    },
    name: valkeyHealthCheckKey);
valkey.WithHealthCheck(valkeyHealthCheckKey);

#pragma warning disable ASPIRECERTIFICATES001
var redisInsight = builder.AddContainer("redisinsight", "redislabs/redisinsight:latest")
    .WithEnvironment("RI_ACCEPT_TERMS_AND_CONDITIONS", "true") // Don't prompt to accept T&C on each start-up
    .WithEnvironment("RI_PRE_SETUP_DATABASES_PATH", "/ri-config/databases.json")
    .WithEnvironment("RI_STDOUT_LOGGER", "true")
    .WithContainerFiles("/ri-config",
        async (context, cancellationToken) =>
        {
            await Task.CompletedTask;
            var dbId = Guid.NewGuid().ToString();
            var databases = new[]
            {
                new
                {
                    id = dbId,
                    host = "redis",
                    port = 6379,
                    name = valkey.Resource.Name,
                    tls = true,
                    verifyServerCert = true,
                    username = "redisinsight.commonname",
                    caCert = new { certificate = mtlsCertificateAuthority.ExportCertificatePem() },
                    clientCert = new
                    {
                        certificate = insightsCert.ExportCertificatePem(),
                        key = insightsCert.ExportPrivateKeyPem(),
                    },
                },
            };
            return
            [
                new ContainerFile
                {
                    Name = "databases.json",
                    Contents = JsonSerializer.Serialize(databases),
                },
            ];
        })
    .WithEndpoint(endpointName: "http",
        callback: static endpoint =>
        {
            endpoint.Transport = "http";
            endpoint.UriScheme = "https";
            endpoint.TargetPort = 5540;
        })
    .WithRelationship(valkey, "RedisInsight")
    .WithCertificateTrustConfiguration(_ => Task.CompletedTask)
    .WithHttpsCertificateConfiguration(ctx =>
    {
        ctx.EnvironmentVariables["RI_SERVER_TLS_CERT"] = ctx.CertificatePath;
        ctx.EnvironmentVariables["RI_SERVER_TLS_KEY"] = ctx.KeyPath;

        if (ctx.Password != null)
        {
            var resourceLogger = ctx.ExecutionContext.Services.GetRequiredService<ResourceLoggerService>();
            var logger = resourceLogger.GetLogger(ctx.Resource);
            logger.LogError("Cannot configure an encrypted certificate for redis insight resource '{resourceName}'", ctx.Resource.Name);
        }

        return Task.CompletedTask;
    })
    .WithCertificateTrustScope(CertificateTrustScope.System)
    .ExcludeFromManifest();

// Vector.io for events aggregation / forwarding. Relay Proxy does not have a robust 
// events system, so having an intentional events collector can improve the solution.
// Could potentially be replaced by a traditional OTEL Collector.
var vector = builder.AddContainer("vector", "timberio/vector:latest-alpine")
    .WithVolume("vector-events-buffer", "/var/lib/vector")
    .WithEnvironment("LAUNCHDARKLY_EVENTS", externalLaunchDarklyEvents)
    .WithContainerFiles("/etc/vector",
        async (context, cancellationToken) =>
        {
            return [
                new ContainerFile
                {
                    Name = "vector.yaml",
                    Contents =
                        """
                        data_dir: /var/lib/vector

                        api:
                          enabled: true
                          address: 0.0.0.0:8686

                        sources:
                          ld_events:
                            type: http_server
                            address: 0.0.0.0:8888
                            # Accept any path so all LD event endpoints are handled:
                            # /bulk, /events/bulk, /mobile/events/bulk, etc.
                            strict_path: false
                            decoding:
                              codec: bytes
                            headers:
                              - authorization
                              - x-launchdarkly-payload-id
                              - x-launchdarkly-event-schema
                              - x-launchdarkly-tags

                          vector_logs:
                            type: internal_logs

                          vector_metrics:
                            type: internal_metrics

                        transforms:
                          prepare_forward:
                            type: remap
                            inputs:
                              - ld_events
                            source: |-
                              # Use get() for hyphenated field names; .authorization is directly accessible
                              .fwd_auth = string(.authorization) ?? ""
                              if .fwd_auth == "" { log("Missing or empty Authorization header — event will be forwarded without auth", level: "warn") }
                              .fwd_payload_id = string(get(., ["x-launchdarkly-payload-id"]) ?? null) ?? ""
                              .fwd_event_schema = string(get(., ["x-launchdarkly-event-schema"]) ?? null) ?? ""
                              .fwd_tags = string(get(., ["x-launchdarkly-tags"]) ?? null) ?? ""

                          # Reshape internal Vector log events into the OTLP JSON Logs structure
                          # so the http sink can POST them directly to the OTLP /v1/logs endpoint.
                          vector_logs_to_otlp:
                            type: remap
                            inputs:
                              - vector_logs
                            source: |-
                              severity_number = if .metadata.level == "error" { 17 } else if .metadata.level == "warn" { 13 } else if .metadata.level == "info" { 9 } else if .metadata.level == "debug" { 5 } else { 1 }
                              time_nanos = to_unix_timestamp!(.timestamp, unit: "nanoseconds")
                              . = {
                                "resourceLogs": [{
                                  "resource": {
                                    "attributes": [
                                      {"key": "service.name", "value": {"stringValue": "vector"}}
                                    ]
                                  },
                                  "scopeLogs": [{
                                    "scope": {"name": "vector.internal"},
                                    "logRecords": [{
                                      "timeUnixNano": to_string(time_nanos),
                                      "severityNumber": severity_number,
                                      "severityText": upcase(string(.metadata.level) ?? "info"),
                                      "body": {"stringValue": string(.message) ?? ""}
                                    }]
                                  }]
                                }]
                              }

                        sinks:
                          launchdarkly_events:
                            type: http
                            inputs:
                              - prepare_forward
                            # Mirror the original request path so /bulk, /events/bulk, etc. reach the right endpoint
                            uri: https://events.launchdarkly.com{{ path }}
                            method: post
                            encoding:
                              codec: raw_message
                            batch:
                              # Forward each SDK batch as its own request — do not re-aggregate
                              max_events: 1
                              timeout_secs: 1
                            request:
                              retry_attempts: 20
                              retry_initial_backoff_secs: 1
                              retry_max_duration_secs: 300
                              headers:
                                Authorization: "{{ fwd_auth }}"
                                Content-Type: application/json
                                X-LaunchDarkly-Payload-ID: "{{ fwd_payload_id }}"
                                X-LaunchDarkly-Event-Schema: "{{ fwd_event_schema }}"
                                X-LaunchDarkly-Tags: "{{ fwd_tags }}"
                            buffer:
                              type: disk
                              max_size: 268435488
                              when_full: block

                          console:
                            type: console 
                            inputs:
                              - vector_logs
                            encoding:
                              codec: text

                          # Send Vector's structured logs to Aspire as OTLP/HTTP JSON.
                          # Each event is already wrapped in a full resourceLogs envelope
                          # by the vector_logs_to_otlp transform above.
                          otel:
                            type: http
                            inputs:
                              - vector_logs_to_otlp
                            uri: ${OTEL_EXPORTER_OTLP_ENDPOINT}/v1/logs
                            method: post
                            encoding:
                              codec: json
                            framing:
                              method: bytes
                            batch:
                              max_events: 1
                              timeout_secs: 1
                            request:
                              headers:
                                content-type: application/json
                                x-otlp-api-key: ${OTEL_EXPORTER_OTLP_API_KEY}

                        """
                }
            ];
        })

    .WithOtlpExporter(OtlpProtocol.HttpProtobuf)
    .WithCertificateTrustScope(CertificateTrustScope.System)
    .WithEnvironment("OTEL_EXPORTER_OTLP_API_KEY", builder.Configuration["AppHost:OtlpApiKey"] ?? "notfound?")
    .WithEndpoint(endpointName: "api",
         callback: static endpoint =>
         {
             endpoint.Transport = "http";
             endpoint.UriScheme = "http";
             endpoint.TargetPort = 8686;
         })
    .WithEndpoint(endpointName: "events",
         callback: static endpoint =>
         {
             endpoint.Transport = "http";
             endpoint.UriScheme = "http";
             endpoint.TargetPort = 8888;
         });

var relayProxy = builder.AddContainer("ld-relay", "launchdarkly/ld-relay:9.0.0-rc.4-static-debian12-nonroot-amd64")
    .WithReference(valkey)
    .WithContainerFiles("/ldr",
       async (context, cancellationToken) =>
       {
           var config = new RelayConfig
           {
               Main = {
                   Port = 8030,
                   HeartbeatInterval = TimeSpan.FromSeconds(90),
                   LogLevel = LaunchDarkly.Relay.LogLevel.Info,
               },
               AutoConfig = {
                    // CacheKey = "AutoConfigCacheKey",
                    Key = $"{await launchdarklyRelayAutoConfigKeyParameter.Resource.GetValueAsync(cancellationToken)}",
                    EnvDatastorePrefix = "$CID"
               },
               Events = {
                   SendEvents = false,
                   EventsURI = new Uri("http://localhost/no-where:8888:"), // Prevents ld-relay from sending *any* telemetry.
               },
               OpenTelemetry = {
                   Enabled = true,
                   Protocol = "http"
               },
               // Environment = {
               //     {
               //         "Default Project Test",
               //         new EnvConfig
               //         {
               //             SDKKey = $"{await launchdarklySdkKeyParameter.Resource.GetValueAsync(cancellationToken)}",
               //             Prefix = $"{await launchdarklyEnvironmentIdParameter.Resource.GetValueAsync(cancellationToken)}",
               //         }
               //     }
               // },
           };

           return [
               new ContainerFile
               {
                   Name = "ld-relay.conf",
                   Contents = RelayConfigWriter.Write(config)
               }
           ];
       })
    .WithEnvironment("USE_REDIS", "true")
    .WithEnvironment("REDIS_TLS", "true")
    .WithEnvironment("REDIS_USERNAME", "relay")
    .WithEnvironment("REDIS_PASSWORD", relayPassword)
    .WithEnvironment("STREAM_URI", externalLaunchDarklyStream)
    .WithEnvironment("AUTO_CONFIG_CACHE_KEY", "AutoConfigCacheKey")
    // Use Aspire's CA trust API to distribute the Valkey CA to ld-relay.
    // Aspire sets SSL_CERT_DIR in the container; Go's crypto/x509 reads it on Linux.
    .WithCertificateAuthorityCollection(customMtlsCaBundle)
    .WithEndpoint(endpointName: "http",
         callback: static endpoint =>
         {
             endpoint.Transport = "http";
             endpoint.UriScheme = "http";
             endpoint.TargetPort = 8030;
         })
   .WithOtlpExporter();

// TODO: Add Prometheus and Grafana extensions for visualisting/creating observability dashboards
// builder.AddPrometheus();
// builder.AddGrafana();

// YARP is the single ingress on port 8030.
// Event paths are routed directly to Vector for durable forwarding with disk buffering and retries.
// All other LD SDK paths (streaming, flags, segments, eval) go to the relay proxy.
var vectorEventsEndpoint = vector.GetEndpoint("events");
var relayHttpEndpoint = relayProxy.GetEndpoint("http");

var yarp = builder.AddYarp("gateway")
    .WithHostPort(8030)
    .WithConfiguration(yarp =>
    {
        // LD analytics & diagnostic event endpoints → Vector
        yarp.AddRoute("/bulk/{**catch-all}", vectorEventsEndpoint);
        yarp.AddRoute("/events/bulk/{**catch-all}", vectorEventsEndpoint);
        yarp.AddRoute("/events/diagnostic/{**catch-all}", vectorEventsEndpoint);
        yarp.AddRoute("/mobile/events/{**catch-all}", vectorEventsEndpoint);
        yarp.AddRoute("/diagnostic/{**catch-all}", vectorEventsEndpoint);

        // Everything else (streaming, flag data, eval, etc.) → LD Relay
        yarp.AddRoute("/{**catch-all}", relayHttpEndpoint);
    })
    .WithOtlpExporter()
    .WithEndpoint(endpointName: "http", callback: static endpoint =>
    {
        endpoint.Transport = "http";
        endpoint.UriScheme = "https";
        endpoint.TargetPort = 8030;
    });

builder.AddProject<LaunchDarklyClient>("launchdarkly-app")
    .WithReference(valkey)
    .WithReference(yarp)
    .WaitFor(yarp)
    .WithEnvironment("LAUNCHDARKLY_SDK_KEY", launchdarklySdkKeyParameter)
    .WithEnvironment("REDIS_KEY_PREFIX", launchdarklyEnvironmentIdParameter)
    // Clear Aspire-injected password; ldclient authenticates via mTLS cert CN instead.
    .WithEnvironment("REDIS_PASSWORD", "")
    .WithCertificateAuthorityCollection(customMtlsCaBundle) // Give the app our mTLS Certificate Authority certs
    .WithEnvironment("VALKEY_CLIENT_CERT_B64", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ldClientCert.ExportCertificatePem())))
    .WithEnvironment("VALKEY_CLIENT_KEY_B64", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ldClientCert.ExportPrivateKeyPem())));

builder.Build().Run();
