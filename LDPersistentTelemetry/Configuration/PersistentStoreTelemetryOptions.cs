namespace LDPersistentTelemetry.Configuration;

public class PersistentStoreTelemetryOptions
{
    public static readonly string Section = "PersistentStoreTelemetry";

    /// <summary>
    /// The interval at which to scrape metrics from the persistent store.
    /// This should be set to a value which balances the need for up-to-date metrics, 
    /// without overloading the cache and potentially degrading cache performance.
    /// </summary>
    public TimeSpan ScrapeInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The maximum number of keys to scan through when calculating Big Segment metrics.
    /// Review span `ScanBigSegmentMembership` to see child `DATA redis EVAL` spans how long each scan is taking.
    /// Bigger page sizes will reduce the number of scans needed, but will increase the time taken for each scan.
    /// </summary>
    public int BigSegmentKeyUsagePageSize { get; set; } = 9000;

    /// <summary>
    /// The maximum number of keys to scan through when calculating Environment metrics.
    /// Review span `ScanKeys` to see child `DATA redis EVAL` spans
    /// </summary>
    public int EnvironmentKeyUsagePageSize { get; set; } = 15000;

    public string ClientCertPath { get; set; } = string.Empty;
    public string ClientCertPassword { get; set; } = string.Empty;

    public string ClientPemString { get; set; } = string.Empty;
    public string ClientKeyString { get; set; } = string.Empty;

}
