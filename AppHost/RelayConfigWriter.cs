using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LaunchDarkly.Relay;

/// <summary>
/// Serializes a <see cref="RelayConfig"/> (or individual section POCOs) back to the
/// LaunchDarkly Relay Proxy <c>.conf</c> file format (Git config / gcfg INI).
/// </summary>
/// <remarks>
/// Only non-default values are emitted: <c>null</c> optional fields are skipped, <c>false</c>
/// booleans are skipped (the relay default), and empty strings/lists are skipped.
/// Sections with nothing to write are omitted entirely.
/// </remarks>
public static class RelayConfigWriter
{
    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Serializes the full config to a string.</summary>
    public static string Write(RelayConfig config)
    {
        var sb = new StringBuilder();
        using var sw = new StringWriter(sb);
        Write(config, sw);
        return sb.ToString();
    }

    /// <summary>Serializes the full config to an open <see cref="TextWriter"/>.</summary>
    public static void Write(RelayConfig config, TextWriter writer)
    {
        WriteMain(config.Main, writer);
        WriteAutoConfig(config.AutoConfig, writer);
        WriteOfflineMode(config.OfflineMode, writer);
        WriteEvents(config.Events, writer);
        WriteRedis(config.Redis, writer);
        WriteConsul(config.Consul, writer);
        WriteDynamoDB(config.DynamoDB, writer);
        WriteProxy(config.Proxy, writer);
        WriteHttp(config.HTTP, writer);
        WriteOpenTelemetry(config.OpenTelemetry, writer);

        foreach (var (name, env) in config.Environment)
            WriteEnv(name, env, writer);

        foreach (var (projKey, filters) in config.Filters)
            WriteFilters(projKey, filters, writer);
    }

    /// <summary>Writes the config to a file, overwriting if it exists.</summary>
    public static void WriteFile(RelayConfig config, string path, Encoding? encoding = null)
    {
        using var sw = new StreamWriter(path, append: false, encoding ?? Encoding.UTF8);
        Write(config, sw);
    }

    // -------------------------------------------------------------------------
    // Individual section writers (public for targeted use)
    // -------------------------------------------------------------------------

    public static string WriteMain(MainConfig c) => WriteSection(w => WriteMain(c, w));
    public static string WriteAutoConfig(AutoConfigConfig c) => WriteSection(w => WriteAutoConfig(c, w));
    public static string WriteOfflineMode(OfflineModeConfig c) => WriteSection(w => WriteOfflineMode(c, w));
    public static string WriteEvents(EventsConfig c) => WriteSection(w => WriteEvents(c, w));
    public static string WriteRedis(RedisConfig c) => WriteSection(w => WriteRedis(c, w));
    public static string WriteConsul(ConsulConfig c) => WriteSection(w => WriteConsul(c, w));
    public static string WriteDynamoDB(DynamoDBConfig c) => WriteSection(w => WriteDynamoDB(c, w));
    public static string WriteProxy(ProxyConfig c) => WriteSection(w => WriteProxy(c, w));
    public static string WriteHttp(HttpConfig c) => WriteSection(w => WriteHttp(c, w));
    public static string WriteOpenTelemetry(OpenTelemetryConfig c) => WriteSection(w => WriteOpenTelemetry(c, w));
    public static string WriteEnv(string name, EnvConfig c) => WriteSection(w => WriteEnv(name, c, w));
    public static string WriteFilters(string projKey, FiltersConfig c) => WriteSection(w => WriteFilters(projKey, c, w));

    // -------------------------------------------------------------------------
    // Section implementations
    // -------------------------------------------------------------------------

    private static void WriteMain(MainConfig c, TextWriter w)
    {
        var s = new SectionWriter("Main", w);
        s.Bool("exitOnError", c.ExitOnError);
        s.Bool("exitAlways", c.ExitAlways);
        s.Bool("ignoreConnectionErrors", c.IgnoreConnectionErrors);
        s.Uri("streamUri", c.StreamURI);
        s.Uri("baseUri", c.BaseURI);
        s.Uri("clientSideBaseUri", c.ClientSideBaseURI);
        s.Int("port", c.Port);
        s.Dur("initTimeout", c.InitTimeout);
        s.Dur("gracefulShutdownTimeout", c.GracefulShutdownTimeout);
        s.Dur("heartbeatInterval", c.HeartbeatInterval);
        s.Dur("maxClientConnectionTime", c.MaxClientConnectionTime);
        s.Dur("pingStreamJitterTime", c.PingStreamJitterTime);
        s.Dur("disconnectedStatusTime", c.DisconnectedStatusTime);
        s.Bool("tlsEnabled", c.TLSEnabled);
        s.Str("tlsCert", c.TLSCert);
        s.Str("tlsKey", c.TLSKey);
        s.Str("tlsMinVersion", c.TLSMinVersion);
        s.Enum("logLevel", c.LogLevel);
        s.Bool("bigSegmentsStaleAsDegraded", c.BigSegmentsStaleAsDegraded);
        s.Dur("bigSegmentsStaleThreshold", c.BigSegmentsStaleThreshold);
        s.Dur("expiredCredentialCleanupInterval", c.ExpiredCredentialCleanupInterval);
    }

    private static void WriteAutoConfig(AutoConfigConfig c, TextWriter w)
    {
        var s = new SectionWriter("AutoConfig", w);
        s.Str("key", c.Key);
        s.Str("cacheKey", c.CacheKey);
        s.Str("cacheEncryptionKey", c.CacheEncryptionKey);
        s.Str("envDatastorePrefix", c.EnvDatastorePrefix);
        s.Str("envDatastoreTableName", c.EnvDatastoreTableName);
        s.List("envAllowedOrigin", c.EnvAllowedOrigin);
        s.List("envAllowedHeader", c.EnvAllowedHeader);
    }

    private static void WriteOfflineMode(OfflineModeConfig c, TextWriter w)
    {
        var s = new SectionWriter("OfflineMode", w);
        s.Str("fileDataSource", c.FileDataSource);
        s.Dur("fileDataSourceMonitoringInterval", c.FileDataSourceMonitoringInterval);
        s.Str("envDatastorePrefix", c.EnvDatastorePrefix);
        s.Str("envDatastoreTableName", c.EnvDatastoreTableName);
        s.List("envAllowedOrigin", c.EnvAllowedOrigin);
        s.List("envAllowedHeader", c.EnvAllowedHeader);
    }

    private static void WriteEvents(EventsConfig c, TextWriter w)
    {
        var s = new SectionWriter("Events", w);
        s.Bool("sendEvents", c.SendEvents);
        s.Uri("eventsUri", c.EventsURI);
        s.Dur("flushInterval", c.FlushInterval);
        s.Int("capacity", c.Capacity);
        s.Byte("maxInboundPayloadSize", c.MaxInboundPayloadSize);
    }

    private static void WriteRedis(RedisConfig? c, TextWriter w)
    {
        if (c is null) return;
        var s = new SectionWriter("Redis", w);
        s.Str("host", c.Host);
        s.Int("port", c.Port);
        s.Uri("url", c.URL);
        s.Bool("tls", c.TLS);
        s.Str("username", c.Username);
        s.Str("password", c.Password);
        s.Dur("localTtl", c.LocalTTL);
    }

    private static void WriteConsul(ConsulConfig? c, TextWriter w)
    {
        if (c is null) return;
        var s = new SectionWriter("Consul", w);
        s.Str("host", c.Host);
        s.Dur("localTtl", c.LocalTTL);
        s.Str("token", c.Token);
        s.Str("tokenFile", c.TokenFile);
    }

    private static void WriteDynamoDB(DynamoDBConfig? c, TextWriter w)
    {
        if (c is null) return;
        var s = new SectionWriter("DynamoDB", w);
        s.Bool("enabled", c.Enabled);
        s.Str("tableName", c.TableName);
        s.Uri("url", c.URL);
        s.Dur("localTtl", c.LocalTTL);
    }

    private static void WriteProxy(ProxyConfig c, TextWriter w)
    {
        var s = new SectionWriter("Proxy", w);
        s.Uri("url", c.URL);
        s.Bool("ntlmAuth", c.NTLMAuth);
        s.Str("user", c.User);
        s.Str("password", c.Password);
        s.Str("domain", c.Domain);
        s.List("caCertFiles", c.CACertFiles);
    }

    private static void WriteHttp(HttpConfig c, TextWriter w)
    {
        var s = new SectionWriter("Http", w);
        s.Bool("enableCompression", c.EnableCompression);
        s.Dur("idleConnTimeout", c.IdleConnTimeout);
        s.Int("maxIdleConns", c.MaxIdleConns);
        s.Int("maxIdleConnsPerHost", c.MaxIdleConnsPerHost == 0 ? null : (int?)c.MaxIdleConnsPerHost);
        s.Bool("disableKeepAlives", c.DisableKeepAlives);
    }

    private static void WriteOpenTelemetry(OpenTelemetryConfig? c, TextWriter w)
    {
        if (c is null || !c.Enabled) return;
        var s = new SectionWriter("OpenTelemetry", w);
        s.Bool("enabled", c.Enabled);
        s.Str("protocol", c.Protocol);
    }

    private static void WriteEnv(string name, EnvConfig c, TextWriter w)
    {
        var s = new SectionWriter($"Environment \"{EscapeSubsection(name)}\"", w);
        s.Str("sdkKey", c.SDKKey);
        s.Str("mobileKey", c.MobileKey);
        s.Str("envId", c.EnvID);
        s.Bool("secureMode", c.SecureMode);
        s.Str("prefix", c.Prefix);
        s.Str("tableName", c.TableName);
        s.List("allowedOrigin", c.AllowedOrigin);
        s.List("allowedHeader", c.AllowedHeader);
        s.Enum("logLevel", c.LogLevel);
        s.Dur("ttl", c.TTL);
        s.Str("projKey", c.ProjKey);
    }

    private static void WriteFilters(string projKey, FiltersConfig c, TextWriter w)
    {
        var s = new SectionWriter($"Filters \"{EscapeSubsection(projKey)}\"", w);
        s.List("keys", c.Keys);
    }

    // -------------------------------------------------------------------------
    // Formatters  (also public for use in other tooling)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> as a Go-style duration string,
    /// e.g. <c>1h30m</c>, <c>30s</c>, <c>500ms</c>.
    /// </summary>
    public static string FormatDuration(TimeSpan ts)
    {
        if (ts == TimeSpan.Zero) return "0s";

        var sb = new StringBuilder();
        long ticks = ts.Ticks;

        long hours = ticks / TimeSpan.TicksPerHour;
        if (hours > 0) { sb.Append(hours).Append('h'); ticks -= hours * TimeSpan.TicksPerHour; }

        long minutes = ticks / TimeSpan.TicksPerMinute;
        if (minutes > 0) { sb.Append(minutes).Append('m'); ticks -= minutes * TimeSpan.TicksPerMinute; }

        long seconds = ticks / TimeSpan.TicksPerSecond;
        if (seconds > 0) { sb.Append(seconds).Append('s'); ticks -= seconds * TimeSpan.TicksPerSecond; }

        long ms = ticks / TimeSpan.TicksPerMillisecond;
        if (ms > 0) { sb.Append(ms).Append("ms"); ticks -= ms * TimeSpan.TicksPerMillisecond; }

        // Sub-millisecond (rare in relay config, but complete for round-trip fidelity)
        long us = ticks / 10; // 1 µs = 10 × 100-ns ticks
        if (us > 0) { sb.Append(us).Append("µs"); ticks -= us * 10; }

        if (ticks > 0) sb.Append(ticks * 100).Append("ns");

        return sb.ToString();
    }

    /// <summary>
    /// Formats a byte count as the largest exact binary unit,
    /// e.g. <c>104857600</c> → <c>100MiB</c>.
    /// Falls back to <c>B</c> if no larger unit divides evenly.
    /// </summary>
    public static string FormatBase2Bytes(long bytes)
    {
        if (bytes == 0) return "0B";

        ReadOnlySpan<(string suffix, long divisor)> units =
        [
            ("EiB", 1024L * 1024 * 1024 * 1024 * 1024 * 1024),
            ("PiB", 1024L * 1024 * 1024 * 1024 * 1024),
            ("TiB", 1024L * 1024 * 1024 * 1024),
            ("GiB", 1024L * 1024 * 1024),
            ("MiB", 1024L * 1024),
            ("KiB", 1024L),
        ];

        foreach (var (suffix, divisor) in units)
        {
            if (bytes >= divisor && bytes % divisor == 0)
                return $"{bytes / divisor}{suffix}";
        }

        return $"{bytes}B";
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string WriteSection(Action<TextWriter> write)
    {
        var sb = new StringBuilder();
        using var sw = new StringWriter(sb);
        write(sw);
        return sb.ToString();
    }

    /// <summary>
    /// Escapes <c>\</c> and <c>"</c> in a subsection name so it is safe inside
    /// <c>[Section "name"]</c> headers.
    /// </summary>
    private static string EscapeSubsection(string name)
        => name.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Quotes a value if it contains characters that could be misread:
    /// leading/trailing whitespace, <c>#</c>, <c>;</c>, or <c>"</c>.
    /// </summary>
    private static string QuoteIfNeeded(string v)
    {
        if (v.Length == 0) return "\"\"";

        bool needsQuotes = v[0] == ' ' || v[^1] == ' '
            || v.Contains('#') || v.Contains(';') || v.Contains('"');

        if (!needsQuotes) return v;

        return "\"" + v
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t")
            + "\"";
    }

    // -------------------------------------------------------------------------
    // SectionWriter — lazy header, consistent indentation
    // -------------------------------------------------------------------------

    private sealed class SectionWriter(string header, TextWriter writer)
    {
        private bool _headerWritten;

        private void EnsureHeader()
        {
            if (_headerWritten) return;
            // Blank line before section (except first)
            writer.WriteLine($"[{header}]");
            _headerWritten = true;
        }

        private void Kv(string key, string value)
        {
            EnsureHeader();
            writer.WriteLine($"    {key} = {value}");
        }

        public void Str(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) Kv(key, QuoteIfNeeded(value));
        }

        public void Bool(string key, bool value)
        {
            if (value) Kv(key, "true");
        }

        public void Int(string key, int? value)
        {
            if (value.HasValue) Kv(key, value.Value.ToString(CultureInfo.InvariantCulture));
        }

        public void Uri(string key, System.Uri? value)
        {
            if (value is not null) Kv(key, value.AbsoluteUri);
        }

        public void Dur(string key, TimeSpan? value)
        {
            if (value.HasValue) Kv(key, FormatDuration(value.Value));
        }

        public void Byte(string key, long? value)
        {
            if (value is > 0) Kv(key, FormatBase2Bytes(value.Value));
        }

        public void Enum<T>(string key, T? value) where T : struct, System.Enum
        {
            if (value.HasValue) Kv(key, value.Value.ToString().ToLowerInvariant());
        }

        /// <summary>Each list item is written as a separate key line (idiomatic gcfg).</summary>
        public void List(string key, List<string>? values)
        {
            if (values is null || values.Count == 0) return;
            foreach (var v in values) Kv(key, QuoteIfNeeded(v));
        }
    }
}
