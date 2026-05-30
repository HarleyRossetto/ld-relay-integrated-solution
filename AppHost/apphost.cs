using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using LaunchDarkly.Relay;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var launchdarklyApiKeyParameter = builder.AddParameter(name: "launchdarkly-api-key", secret: true);
var launchdarklyAutoConfigKeyParameter = builder.AddParameter(name: "launchdarkly-auto-config", secret: true);
var launchdarklySdkKeyParameter = builder.AddParameter(name: "launchdarkly-sdk-key", secret: true);
var launchdarklyEnvironmentIdParameter = builder.AddParameter(name: "launchdarkly-environment");

// Generate ephemeral mTLS certs and ACL credentials for Valkey.
// All certs are CA-signed and regenerated on every run.
var ca = MtlsCerts.GenerateCA();
var serverCert = MtlsCerts.IssueServerCert(ca);
var ldclientCert = MtlsCerts.IssueClientCert(ca, "ldclient");

var caPem = MtlsCerts.ExportPem(ca);
var serverPem = MtlsCerts.ExportPem(serverCert);
var ldclientPem = MtlsCerts.ExportPem(ldclientCert);

// Random password for the relay ACL user (relay connects without a client cert).
var relayPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

// Valkey — named "redis" so .WithReference(valkey) injects the expected
// REDIS_HOST / REDIS_PORT env vars that both ld-relay and LaunchDarklyClient read.
var valkey = builder.AddValkey("valkey")
    .WithContainerFiles("/certs",
        async (context, cancellationToken) =>
        {
            await Task.CompletedTask;
            return
            [
                new ContainerFile { Name = "ca.crt",     Contents = caPem.CertPem },
                new ContainerFile { Name = "server.crt", Contents = serverPem.CertPem },
                new ContainerFile { Name = "server.key", Contents = serverPem.KeyPem },
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
                        # Disable plaintext port; serve TLS only on 6379
                        port 0
                        tls-port 6379
                        tls-replication no

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
                    Contents =
                        $"""
                        user default off nopass nocommands resetkeys resetchannels
                        user relay on >{relayPassword} ~* +@all
                        user ldclient on nopass ~{envId}* +@all
                        """
                },
            ];
        });

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

builder.AddContainer("redisinsight", "redislabs/redisinsight:latest")
    .WithEnvironment("RI_ACCEPT_TERMS_AND_CONDITIONS", "true")
    .WithEndpoint(endpointName: "http",
                  callback: static endpoint =>
                  {
                      endpoint.Transport = "http";
                      endpoint.UriScheme = "http";
                      endpoint.TargetPort = 5540;
                  });

var vector = builder.AddContainer("vector", "timberio/vector:latest-alpine")
    .WithVolume("vector-events-buffer", "/var/lib/vector")
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

                        """
                }
            ];
        })
    .WithEndpoint(endpointName: "api",
                  callback: static endpoint =>
                  {
                      endpoint.Transport = "http";
                      endpoint.UriScheme = "http";
                      // endpoint.Port = 8686;
                      endpoint.TargetPort = 8686;
                  })
    .WithEndpoint(endpointName: "events",
                  callback: static endpoint =>
                  {
                      endpoint.Transport = "http";
                      endpoint.UriScheme = "http";
                      // endpoint.Port = 8888;
                      endpoint.TargetPort = 8888;
                  })
    .WithReference(valkey)
    .WithOtlpExporter();

// var relayProxyV8 = builder.AddContainer("ld-relay-v8", "launchdarkly/ld-relay:latest")
//     .WithReference(redis)
//     .WaitFor(vector)
//     .WithContainerFiles("/ldr",
//             async (context, cancellationToken) =>
//             {
//                 var config = new RelayConfig
//                 {
//                     Main = {
//                         Port = 8030,
//                         HeartbeatInterval = TimeSpan.FromSeconds(90),
//                         LogLevel = LogLevel.Info
//                     },
//                     Events = {
//                         SendEvents = false,
//                         EventsURI = new Uri("http://localhost/no-where:8888:"),
//                     },
//                     Environment = {
//                         {
//                             "Default Project Test",
//                             new EnvConfig
//                             {
//                                 SDKKey = "sdk-f04627de-9b1c-43fc-bc21-ddf2ad70adbb",
//                                 Prefix = "690f4c5b1223450a0b65f918",
//                             }
//                         }
//                     },
//                 };
//
//                 return [
//                     new ContainerFile
//                     {
//                         Name = "ld-relay.conf",
//                         Contents = RelayConfigWriter.Write(config)
//                     }
//                 ];
//             })
//     // .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:19116")
//     .WithEnvironment("USE_REDIS", "true")
//     .WithEnvironment("REDIS_TLS", "true")
//     .WithEndpoint(endpointName: "http",
//                   callback: static endpoint =>
//                   {
//                       endpoint.Transport = "http";
//                       endpoint.UriScheme = "http";
//                       endpoint.Port = 8031;        // 8030 is owned by the YARP gateway; direct debug access here
//                       endpoint.TargetPort = 8030;
//                   });

var relayProxy = builder.AddContainer("ld-relay", "launchdarkly/ld-relay:9.0.0-rc.3-static-debian12-nonroot-amd64")
    .WithReference(valkey)
    .WaitFor(vector)
    .WithContainerFiles("/ldr",
            async (context, cancellationToken) =>
            {
                var config = new RelayConfig
                {
                    Main = {
                        Port = 8030,
                        HeartbeatInterval = TimeSpan.FromSeconds(90),
                        LogLevel = LogLevel.Info
                    },
                    Events = {
                        // When useYarpEventRouting is false, relay proxies events to Vector
                        // so Vector can handle durable forwarding to LaunchDarkly with retries.
                        // When true, YARP intercepts event endpoints before they reach relay,
                        // so relay should not forward events at all.
                        SendEvents = false,
                        EventsURI = new Uri("http://localhost/no-where:8888:"),
                        // Use the container-internal hostname — GetEndpoint().Url returns the host-mapped
                        // localhost URL which is unreachable from within the Docker network.
                        // EventsURI = new Uri($"http://vector:{vector.GetEndpoint("events").TargetPort}"),
                        // useYarpEventRouting
                        //     ? new Uri("https://events.launchdarkly.com")   // unused; YARP routes away from relay
                        //     : new Uri("http://vector:8888"),               // relay → Vector → LaunchDarkly
                    //     FlushInterval = TimeSpan.FromSeconds(5),
                    //     Capacity = 1000
                    },
                    OpenTelemetry = {
                        Enabled = true,
                        Protocol = "http"
                    },
                    Environment = {
                        {
                            "Default Project Test",
                            new EnvConfig
                            {
                                SDKKey = "sdk-f04627de-9b1c-43fc-bc21-ddf2ad70adbb",
                                Prefix = "690f4c5b1223450a0b65f918",
                            }
                        }
                    },
                };

                return [
                    new ContainerFile
                    {
                        Name = "ld-relay.conf",
                        Contents = RelayConfigWriter.Write(config)
                    }
                ];
            })
    // .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:19116")
    .WithEnvironment("USE_REDIS", "true")
    .WithEnvironment("REDIS_TLS", "true")
    .WithEnvironment("REDIS_USERNAME", "relay")
    .WithEnvironment("REDIS_PASSWORD", relayPassword)
    .WithEndpoint(endpointName: "http",
                  callback: static endpoint =>
                  {
                      endpoint.Transport = "http";
                      endpoint.UriScheme = "http";
                      // endpoint.Port = 8031;        // 8030 is owned by the YARP gateway; direct debug access here
                      endpoint.TargetPort = 8030;
                  })
   .WithOtlpExporter();

// Inject the Valkey CA into /usr/local/share/ca-certificates/ inside the relay container.
// Go's crypto/x509 reads individual PEM files from this directory in addition to the
// system bundle at /etc/ssl/certs/ca-certificates.crt, so mounting here doesn't clobber
// the existing system CA certs (unlike mounting to /etc/ssl/certs/ directly).
relayProxy.WithContainerFiles("/usr/local/share/ca-certificates",
    async (_, _) =>
    {
        await Task.CompletedTask;
        return [new ContainerFile { Name = "valkey-ca.crt", Contents = caPem.CertPem }];
    });

// YARP is the single ingress on port 8030.
// Event paths are routed directly to Vector for durable forwarding with disk buffering and retries.
// All other LD SDK paths (streaming, flags, segments, eval) go to the relay proxy.
var vectorEventsEndpoint = vector.GetEndpoint("events");
var relayHttpEndpoint = relayProxy.GetEndpoint("http");

var yarp = builder.AddYarp("gateway")
    .WithHostPort(8030)
    .WaitFor(relayProxy)
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
    .WithEndpoint(endpointName: "http",
                  callback: static endpoint =>
                  {
                      endpoint.Transport = "http";
                      endpoint.UriScheme = "http";
                      // endpoint.Port = 8030;
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
    .WithEnvironment("VALKEY_CA_CERT_B64", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(caPem.CertPem)))
    .WithEnvironment("VALKEY_CLIENT_CERT_B64", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ldclientPem.CertPem)))
    .WithEnvironment("VALKEY_CLIENT_KEY_B64", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ldclientPem.KeyPem)));


builder.Build().Run();
