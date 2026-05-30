namespace LaunchDarkly.Relay;

/// <summary>
/// Describes the full configuration for a Relay Proxy instance.
/// </summary>
public sealed class RelayConfig
{
    public MainConfig Main { get; set; } = new();
    public AutoConfigConfig AutoConfig { get; set; } = new();
    public OfflineModeConfig OfflineMode { get; set; } = new();
    public EventsConfig Events { get; set; } = new();
    public RedisConfig? Redis { get; set; } = null;
    public ConsulConfig? Consul { get; set; } = null;
    public DynamoDBConfig? DynamoDB { get; set; } = null;
    public Dictionary<string, EnvConfig> Environment { get; set; } = new();
    public Dictionary<string, FiltersConfig> Filters { get; set; } = new();
    public ProxyConfig Proxy { get; set; } = new();
    public HttpConfig HTTP { get; set; } = new();
    public OpenTelemetryConfig OpenTelemetry { get; set; } = new();
}

/// <summary>
/// Global configuration options for Relay. Corresponds to the [Main] section in the config file.
/// </summary>
public sealed class MainConfig
{
    // ENV: EXIT_ON_ERROR
    public bool ExitOnError { get; set; }

    // ENV: EXIT_ALWAYS
    public bool ExitAlways { get; set; }

    // ENV: IGNORE_CONNECTION_ERRORS
    public bool IgnoreConnectionErrors { get; set; }

    // ENV: STREAM_URI
    public Uri? StreamURI { get; set; } = new Uri("https://stream.launchdarkly.com");

    // ENV: BASE_URI
    public Uri? BaseURI { get; set; } = new Uri("https://sdk.launchdarkly.com");

    // ENV: CLIENT_SIDE_BASE_URI
    public Uri? ClientSideBaseURI { get; set; } = new Uri("https://clientsdk.launchdarkly.com");

    // ENV: PORT
    public int? Port { get; set; } = 8030;

    // ENV: INIT_TIMEOUT
    public TimeSpan? InitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // ENV: GRACEFUL_SHUTDOWN_TIMEOUT
    public TimeSpan? GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // ENV: HEARTBEAT_INTERVAL
    public TimeSpan? HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(3);

    // ENV: MAX_CLIENT_CONNECTION_TIME
    public TimeSpan? MaxClientConnectionTime { get; set; }

    // ENV: PING_STREAM_JITTER_TIME
    public TimeSpan? PingStreamJitterTime { get; set; }

    // ENV: DISCONNECTED_STATUS_TIME
    public TimeSpan? DisconnectedStatusTime { get; set; } = TimeSpan.FromMinutes(1);

    // ENV: TLS_ENABLED
    public bool TLSEnabled { get; set; }

    // ENV: TLS_CERT
    public string? TLSCert { get; set; }

    // ENV: TLS_KEY
    public string? TLSKey { get; set; }

    // ENV: TLS_MIN_VERSION — e.g. "1.2", "1.3"
    public string? TLSMinVersion { get; set; }

    // ENV: LOG_LEVEL
    public LogLevel? LogLevel { get; set; }

    // ENV: BIG_SEGMENTS_STALE_AS_DEGRADED
    public bool BigSegmentsStaleAsDegraded { get; set; }

    // ENV: BIG_SEGMENTS_STALE_THRESHOLD
    public TimeSpan? BigSegmentsStaleThreshold { get; set; } = TimeSpan.FromMinutes(5);

    // ENV: EXPIRED_CREDENTIAL_CLEANUP_INTERVAL
    public TimeSpan? ExpiredCredentialCleanupInterval { get; set; }
}

/// <summary>
/// Configuration for the auto-configuration feature.
/// </summary>
public sealed class AutoConfigConfig
{
    // ENV: AUTO_CONFIG_KEY
    public string? Key { get; set; }

    // ENV: ENV_DATASTORE_PREFIX
    public string? EnvDatastorePrefix { get; set; }

    // ENV: ENV_DATASTORE_TABLE_NAME
    public string? EnvDatastoreTableName { get; set; }

    // ENV: ENV_ALLOWED_ORIGIN
    public List<string>? EnvAllowedOrigin { get; set; }

    // ENV: ENV_ALLOWED_HEADER
    public List<string>? EnvAllowedHeader { get; set; }

    // ENV: AUTO_CONFIG_CACHE_KEY
    public string? CacheKey { get; set; }

    // ENV: AUTO_CONFIG_CACHE_ENCRYPTION_KEY
    public string? CacheEncryptionKey { get; set; }
}

/// <summary>
/// Configuration for the offline / file data source feature.
/// </summary>
public sealed class OfflineModeConfig
{
    // ENV: FILE_DATA_SOURCE
    public string? FileDataSource { get; set; }

    // ENV: FILE_DATA_SOURCE_MONITORING_INTERVAL
    public TimeSpan? FileDataSourceMonitoringInterval { get; set; }

    // ENV: ENV_DATASTORE_PREFIX
    public string? EnvDatastorePrefix { get; set; }

    // ENV: ENV_DATASTORE_TABLE_NAME
    public string? EnvDatastoreTableName { get; set; }

    // ENV: ENV_ALLOWED_ORIGIN
    public List<string>? EnvAllowedOrigin { get; set; }

    // ENV: ENV_ALLOWED_HEADER
    public List<string>? EnvAllowedHeader { get; set; }
}

/// <summary>
/// Configuration for event proxying.
/// </summary>
public sealed class EventsConfig
{
    // ENV: EVENTS_HOST
    public Uri? EventsURI { get; set; } = new Uri("https://events.launchdarkly.com");

    // ENV: USE_EVENTS
    public bool SendEvents { get; set; }

    // ENV: EVENTS_FLUSH_INTERVAL
    public TimeSpan? FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    // ENV: EVENTS_CAPACITY
    public int? Capacity { get; set; } = 1000;

    // ENV: EVENTS_MAX_INBOUND_PAYLOAD_SIZE — size in bytes (base-2)
    public long? MaxInboundPayloadSize { get; set; }
}

/// <summary>
/// Configuration for the optional Redis integration.
/// Enabled when URL or Host is non-empty, or Port is set.
/// </summary>
public sealed class RedisConfig
{
    // ENV: REDIS_HOST
    public string? Host { get; set; }

    public int? Port { get; set; }

    // ENV: REDIS_URL
    public Uri? URL { get; set; }

    // ENV: CACHE_TTL
    public TimeSpan? LocalTTL { get; set; } = TimeSpan.FromSeconds(30);

    // ENV: REDIS_TLS
    public bool TLS { get; set; }

    // ENV: REDIS_USERNAME
    public string? Username { get; set; }

    // ENV: REDIS_PASSWORD
    public string? Password { get; set; }
}

/// <summary>
/// Configuration for the optional Consul integration.
/// Enabled when Host is non-empty.
/// </summary>
public sealed class ConsulConfig
{
    // ENV: CONSUL_HOST
    public string? Host { get; set; }

    // ENV: CACHE_TTL
    public TimeSpan? LocalTTL { get; set; } = TimeSpan.FromSeconds(30);

    // ENV: CONSUL_TOKEN
    public string? Token { get; set; }

    // ENV: CONSUL_TOKEN_FILE
    public string? TokenFile { get; set; }
}

/// <summary>
/// Configuration for the optional DynamoDB integration.
/// Enabled when Enabled is true.
/// </summary>
public sealed class DynamoDBConfig
{
    // ENV: USE_DYNAMODB
    public bool Enabled { get; set; }

    // ENV: DYNAMODB_TABLE
    public string? TableName { get; set; }

    // ENV: DYNAMODB_URL
    public Uri? URL { get; set; }

    // ENV: CACHE_TTL
    public TimeSpan? LocalTTL { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Describes a single environment to be relayed. Corresponds to an [environment "name"] section.
/// </summary>
public sealed class EnvConfig
{
    // ENV: LD_ENV_envname
    public string? SDKKey { get; set; }

    // ENV: LD_MOBILE_KEY_envname
    public string? MobileKey { get; set; }

    // ENV: LD_CLIENT_SIDE_ID_envname
    public string? EnvID { get; set; }

    // ENV: LD_PREFIX_envname — used with Redis, Consul, or DynamoDB
    public string? Prefix { get; set; }

    // ENV: LD_TABLE_NAME_envname — used with DynamoDB
    public string? TableName { get; set; }

    // ENV: LD_ALLOWED_ORIGIN_envname
    public List<string>? AllowedOrigin { get; set; }

    // ENV: LD_ALLOWED_HEADER_envname
    public List<string>? AllowedHeader { get; set; }

    // ENV: LD_SECURE_MODE_envname
    public bool SecureMode { get; set; }

    // ENV: LD_LOG_LEVEL_envname
    public LogLevel? LogLevel { get; set; }

    // ENV: LD_TTL_envname
    public TimeSpan? TTL { get; set; }

    // ENV: LD_PROJ_KEY_envname
    public string? ProjKey { get; set; }

    /// <summary>Injected from the [filters] section; not set from an environment variable.</summary>
    public string? FilterKey { get; set; }

    /// <summary>True when this environment was created in offline mode.</summary>
    public bool Offline { get; set; }
}

/// <summary>
/// Payload filter keys for an environment. Corresponds to a [filters "name"] section.
/// </summary>
public sealed class FiltersConfig
{
    // ENV: LD_FILTER_KEYS_envname
    public List<string>? Keys { get; set; }
}

/// <summary>
/// Proxy server options.
/// </summary>
public sealed class ProxyConfig
{
    // ENV: PROXY_URL
    public Uri? URL { get; set; }

    // ENV: PROXY_AUTH_NTLM
    public bool NTLMAuth { get; set; }

    // ENV: PROXY_AUTH_USER
    public string? User { get; set; }

    // ENV: PROXY_AUTH_PASSWORD
    public string? Password { get; set; }

    // ENV: PROXY_AUTH_DOMAIN
    public string? Domain { get; set; }

    // ENV: PROXY_CA_CERTS
    public List<string>? CACertFiles { get; set; }
}

/// <summary>
/// HTTP-related configuration options. Corresponds to the [Http] section in the config file.
/// </summary>
public sealed class HttpConfig
{
    // ENV: HTTP_ENABLE_COMPRESSION
    public bool EnableCompression { get; set; }

    // ENV: HTTP_IDLE_CONN_TIMEOUT
    public TimeSpan? IdleConnTimeout { get; set; }

    // ENV: HTTP_MAX_IDLE_CONNS
    public int? MaxIdleConns { get; set; }

    // ENV: HTTP_MAX_IDLE_CONNS_PER_HOST
    public int MaxIdleConnsPerHost { get; set; }

    // ENV: HTTP_DISABLE_KEEPALIVE
    public bool DisableKeepAlives { get; set; }
}

/// <summary>
/// Configuration for the optional OTLP export integration. Corresponds to the [OpenTelemetry] section.
/// Standard OTEL environment variables (OTEL_EXPORTER_OTLP_ENDPOINT, etc.) are read directly by the SDK.
/// </summary>
public sealed class OpenTelemetryConfig
{
    // ENV: USE_OTLP
    public bool Enabled { get; set; }

    // ENV: OTEL_EXPORTER_OTLP_PROTOCOL — e.g. "grpc", "http/protobuf"
    public string? Protocol { get; set; }
}

/// <summary>Log verbosity level.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    None,
}
