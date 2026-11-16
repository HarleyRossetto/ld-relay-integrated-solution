using System.Diagnostics;
using System.Diagnostics.Metrics;
using StackExchange.Redis;
using LDPersistentTelemetry.Models;
using LDPersistentTelemetry.Context;
using Microsoft.Extensions.Options;
using LDPersistentTelemetry.Configuration;

namespace LDPersistentTelemetry.Services;

public sealed class RelayProxyMetricScraper(ILogger<RelayProxyMetricScraper> logger,
                              IConnectionMultiplexer redis,
                              IMeterFactory meterFactory,
                              IRelayContextProvider relayContextProvider,
                              IOptionsMonitor<PersistentStoreTelemetryOptions> optionsMonitor) : BackgroundService
{

    #region OTLP
    private readonly Meter _meter = meterFactory.Create("ldrelay.persistence", "1.0.0");
    private static readonly ActivitySource MetricScraperSource = new("ldrelay.persistence", "1.0.0");
    #endregion OTLP

    private RelayProxyAutoConfigCache? _relayContext;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {


        logger.LogInformation("RelayProxyMetricScraper background service is starting.");

        var connectedClients = _meter.CreateGauge<int>(
            "users.connected",
            description: "Number of connected clients per user"
        );

        var idleSeconds = _meter.CreateGauge<int>(
            "users.idle_seconds",
            description: "Number of seconds a connected client has been idle for",
            unit: "s"
        );

        var networkBytesOut = _meter.CreateGauge<long>(
            "users.network_bytes_out",
            description: "Total network bytes sent per user",
            unit: "By"
        );

        var networkBytesIn = _meter.CreateGauge<long>(
            "users.network_bytes_in",
            description: "Total network bytes received per user",
            unit: "By"
        );

        var keyUsage = _meter.CreateGauge<long>(
            "keys.environment.usage",
            description: "Number of keys per environment prefix"
        );

        var bigSegmentMembershipCount = _meter.CreateGauge<long>(
            "keys.big_segment.usage",
            description: "Number of contexts in big segments per environment"
        );

        // Get initial context
        logger.LogInformation("Fetching Relay Proxy context");
        _relayContext = await relayContextProvider.GetRelayContextAsync(stoppingToken);

        using var timer = new PeriodicTimer(optionsMonitor.CurrentValue.ScrapeInterval);

        using var optionsChangeRegistration = optionsMonitor.OnChange((newOptions, _) =>
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("PersistentStoreTelemetryOptions changed, updating scrape interval to {ScrapeInterval}.", newOptions.ScrapeInterval);
            }
            timer.Period = newOptions.ScrapeInterval;
        });

        try
        {
            // WaitForNextTickAsync waits asynchronously for the next interval
            while (!stoppingToken.IsCancellationRequested &&
                   await timer.WaitForNextTickAsync(stoppingToken))
            {

                await Scrape(connectedClients,
                             idleSeconds,
                             networkBytesIn,
                             networkBytesOut,
                             keyUsage,
                             bigSegmentMembershipCount,
                             stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("RelayProxyMetricScraper background service is stopping due to cancellation.");
        }
    }

    readonly struct RedisContext
    {
        public required IConnectionMultiplexer RedisConnection { get; init; }
        public required IDatabase Database { get; init; }
        public required IServer Server { get; init; }
        public required System.Net.EndPoint[] Endpoints { get; init; }
    }

    private static List<AuxillaryClientInfo> ParseDetailedClientInfo(ClientInfo[] clients)
    {
        var detailedClients = new List<AuxillaryClientInfo>();

        foreach (var client in clients)
        {
            detailedClients.Add(AuxillaryClientInfo.FromClientInfo(client));
        }

        return detailedClients;
    }

    private async Task Scrape(Gauge<int> connectedClients,
                              Gauge<int> idleSeconds,
                              Gauge<long> networkBytesIn,
                              Gauge<long> networkBytesOut,
                              Gauge<long> keyUsage,
                              Gauge<long> bigSegmentMembershipCount,
                              CancellationToken stoppingToken)
    {

        using Activity? activity = MetricScraperSource.StartActivity("Scrape");

        var redisContext = new RedisContext
        {
            RedisConnection = redis,
            Database = redis.GetDatabase(),
            Endpoints = redis.GetEndPoints(),
            Server = redis.GetServer(redis.GetEndPoints()[0])
        };

        await CalculateUserUsage(redisContext,
                                 connectedClients,
                                 idleSeconds,
                                 networkBytesIn,
                                 networkBytesOut,
                                 stoppingToken);

        await CalculateKeyUsage(redisContext, keyUsage, bigSegmentMembershipCount, stoppingToken);
    }

    private async static Task CalculateUserUsage(RedisContext ctx,
                                          Gauge<int> connectedClients,
                                          Gauge<int> idleSeconds,
                                          Gauge<long> networkBytesIn,
                                          Gauge<long> networkBytesOut,
                                          CancellationToken cancellationToken = default)
    {
        // NOTE: We assume there is a single Redis server available. 
        //       If there is a master+relica setup, then that will not currently be considered.
        //       We will assume that such setups only forward reading traffic to the replica, that that will be the scraped instance.

        ClientInfo[] clients = ctx.Server.ClientList();

        var serverTag = new KeyValuePair<string, object?>("server", ctx.Server.EndPoint.ToString());

        using var parseActivity = MetricScraperSource.StartActivity("ParseClientInfo");
        var detailedClients = ParseDetailedClientInfo(clients);
        parseActivity?.Stop();

        var clientMetricActivity = MetricScraperSource.StartActivity("RecordClientMetrics");
        foreach (var group in detailedClients.GroupBy(c => c.User))
        {
            var user = group.Key;
            var count = group.Count();

            var userTag = new KeyValuePair<string, object?>("user", user);

            connectedClients.Record(count, [serverTag, userTag]);

            var totalNetIn = 0L;
            var totalNetOut = 0L;
            foreach (var client in group)
            {
                totalNetIn += client.TotalNetworkIn ?? 0;
                totalNetOut += client.TotalNetworkOut ?? 0;

                idleSeconds.Record(client.ClientInfo.IdleSeconds, serverTag, userTag);
            }

            networkBytesIn.Record(totalNetIn, serverTag, userTag);
            networkBytesOut.Record(totalNetOut, serverTag, userTag);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        clientMetricActivity?.Stop();
    }
    private async Task CalculateKeyUsage(RedisContext ctx, Gauge<long> keyUsage, Gauge<long> bigSegmentMembershipCount, CancellationToken cancellationToken = default)
    {
        using Activity? activity = MetricScraperSource.StartActivity("CalculateKeyUsage");

        var scanActivity = MetricScraperSource.StartActivity("ScanKeys");

        // Iterate through all keys and accumulate counts based on their prefixes (environment keys)
        // TODO: PageSize should be configurable through IOptions
        // foreach (var key in ctx.Server.Keys(pageSize: 5000))
        // {
        //     var keyString = key.ToString();
        //     var prefix = keyString.Split(':')[0];
        //
        //     usageDictionary.TryGetValue(prefix, out var count);
        //     usageDictionary[prefix] = count + 1;
        //
        //     if (cancellationToken.IsCancellationRequested)
        //     {
        //         return;
        //     }
        // }
        var usageDictionary = await RunLuaAsyncKeyUsage(ctx);
        scanActivity?.Stop();

        // Now we have the counts, we lookup their environment information from the relay context and record the metrics with appropriate tags
        var keyUsageRecordActivity = MetricScraperSource.StartActivity("RecordKeyUsageMetrics");
        foreach (var kvp in usageDictionary)
        {
            var prefix = kvp.Key;
            var count = kvp.Value;

            if (_relayContext?.Environments.TryGetValue(prefix, out var environment) ?? false)
            {
                var envKey = environment?.Key ?? "unknown";
                var projectKey = environment?.ProjectKey ?? "unknown";

                var envTag = new KeyValuePair<string, object?>("envKey", envKey);
                var projectTag = new KeyValuePair<string, object?>("projectKey", projectKey);
                var clientSideIdTag = new KeyValuePair<string, object?>("clientSideId", environment?.ID ?? "unknown");

                keyUsage.Record(count, [envTag, projectTag, clientSideIdTag]);
            }
            else
            {
                // If we don't have the environment information, we can still record the metric with just the prefix as a tag
                var prefixTag = new KeyValuePair<string, object?>("prefix", prefix);
                keyUsage.Record(count, [prefixTag]);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
        keyUsageRecordActivity?.Stop();



        // NOTE: SECOND IMPLEMENTATION USING CONCURRENT DICTIONARY AND TASK PARALLELISM
        //
        // var bigSegmentScanActivity = MetricScraperSource.StartActivity("ScanBigSegmentMembership");
        // var counts = new ConcurrentDictionary<(string env, string kind, string member), int>();
        // var semaphore = new SemaphoreSlim(50); // 50 concurrent SSCAN calls
        //
        // var bigSegmentKeys = ctx.Server.Keys(pattern: "*:big_segment_*:*", pageSize: 10000)
        //     .Select(key => (key, parts: key.ToString().Split(':', 3)))
        //     .Where(x => x.parts.Length == 3)
        //     .ToList();
        //
        // var tasks = bigSegmentKeys.Select(async x =>
        // {
        //     await semaphore.WaitAsync(cancellationToken);
        //     try
        //     {
        //         var members = await ctx.Database.SetMembersAsync(x.key);
        //         foreach (var member in members)
        //         {
        //             var k = (x.parts[0], x.parts[1], member.ToString());
        //             counts.AddOrUpdate(k, 1, (_, n) => n + 1);
        //         }
        //     }
        //     finally { semaphore.Release(); }
        // });
        //
        // logger.LogInformation("Scanning {Count} big segment keys with up to 50 concurrent SSCAN calls.", bigSegmentKeys.Count);
        // await Task.WhenAll(tasks);
        //
        // bigSegmentScanActivity?.Stop();
        //


        // NOTE: ORIGINAL BRUTE FORCE IMLEMENTATION
        //
        // Big Segment Membership metrics - this could be more expensive, so maybe have a flag to enable / disable this.
        // var counts = new Dictionary<(string env, string kind, string member), int>();
        // // TODO: PageSize should be configurable through IOptions
        // foreach (var key in ctx.Server.Keys(pattern: "*:big_segment_*:*", pageSize: 10000))
        // {
        //     string s = key.ToString();
        //
        //     var parts = s.Split(':', 3); // envId, big_segment_include|exclude, hash
        //     if (parts.Length < 3) continue;
        //
        //     var env = parts[0];
        //     var kind = parts[1]; // big_segment_include / big_segment_exclude
        //
        //     foreach (var member in ctx.Database.SetScan(key, pageSize: 10000))
        //     {
        //         var k = (env, kind, member.ToString());
        //         counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
        //
        //
        //         if (cancellationToken.IsCancellationRequested)
        //         {
        //             return;
        //         }
        //     }
        //
        //     if (cancellationToken.IsCancellationRequested)
        //     {
        //         return;
        //     }
        // }
        // bigSegmentScanActivity?.Stop();


        // NOTE: THIRD IMPLEMENTATION USING PARALLEL.FOREACH.ASYNC
        // var bigSegmentScanActivity = MetricScraperSource.StartActivity("ScanBigSegmentMembership");
        // var counts = new ConcurrentDictionary<(string env, string kind, string segment), int>();
        //
        // await Parallel.ForEachAsync(
        //     ctx.Server.Keys(pattern: "*:big_segment_*:*", pageSize: 10000),
        //     new ParallelOptions { MaxDegreeOfParallelism = 50, CancellationToken = cancellationToken },
        //     async (key, ct) =>
        //     {
        //         var parts = key.ToString().Split(':', 3);
        //         if (parts.Length < 3) return;
        //
        //         var members = await ctx.Database.SetMembersAsync(key);
        //         foreach (var member in members)
        //         {
        //             counts.AddOrUpdate(
        //                 (parts[0], parts[1], member.ToString()),
        //                 1,
        //                 (_, n) => n + 1);
        //         }
        //     }
        // );


        // NOTE: FOURTH IMPLEMENTATION USING LUA SCRIPT
        var bigSegmentScanActivity = MetricScraperSource.StartActivity("ScanBigSegmentMembership");
        var result = await RunLuaAsync(ctx);
        bigSegmentScanActivity?.Stop();

        var bigSegmentRecordActivity = MetricScraperSource.StartActivity("RecordBigSegmentMembershipMetrics");
        foreach (var kvp in result)
        {
            string projectKey = "unknown";
            string environmentKey = "unknown";
            string clientSideId = "unknown";

            var (envId, kind, member) = ParseBigSegmentKey(kvp.Key);

            if (_relayContext?.Environments.TryGetValue(envId, out var environment) ?? false)
            {
                environmentKey = environment?.Key ?? "unknown";
                projectKey = environment?.ProjectKey ?? "unknown";
                clientSideId = environment?.ID ?? "unknown";
            }

            var envTag = new KeyValuePair<string, object?>("envKey", environmentKey);
            var projectTag = new KeyValuePair<string, object?>("projectKey", projectKey);
            var kindTag = new KeyValuePair<string, object?>("kind", kind);
            var segmentTag = new KeyValuePair<string, object?>("segment", member);
            var clientSideIdTag = new KeyValuePair<string, object?>("clientSideId", clientSideId);

            bigSegmentMembershipCount.Record(kvp.Value, [envTag, projectTag, segmentTag, kindTag, clientSideIdTag]);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
        bigSegmentRecordActivity?.Stop();


    }

    private static (string envrionmentId, string kind, string member) ParseBigSegmentKey(string key)
    {
        var parts = key.Split('|', 3);
        if (parts.Length < 3)
        {
            throw new ArgumentException($"Invalid big segment key format: {key}");
        }
        return (parts[0], parts[1], parts[2]);
    }

    private async Task<Dictionary<string, long>> RunLuaAsyncKeyUsage(RedisContext ctx)
    {
        var luaSource = @"
                          --Args: ARGV[1] = cursor, ARGV[2] = page size
                          -- Returns: { next_cursor, { prefix, count, ...} }
                          local result = redis.call('SCAN', @cursor, 'COUNT', @pageSize)
                          local newCursor = result[1]
                          local keys = result[2]
                          local counts = {}
                          
                          for _, key in ipairs(keys) do
                              local colon = string.find(key, ':', 1, true)
                              if colon then
                                  local prefix = string.sub(key, 1, colon - 1)
                                  counts[prefix] = (counts[prefix] or 0) + 1
                              end
                          end
                          
                          local flat = {}
                          for k, v in pairs(counts) do
                              table.insert(flat, k)
                              table.insert(flat, tostring(v))
                          end
                          
                          return {newCursor, flat}
                        ";
        var script = LuaScript.Prepare(luaSource);
        var loaded = script.Load(ctx.Server);

        if (loaded is null)
        {
            logger.LogError("Failed to load Lua script for key usage calculation, unable to calculate key usage metrics.");
            return [];
        }

        string cursor = "0";
        var totalCounts = new Dictionary<string, long>();

        do
        {
            var evaluationResult = await loaded.EvaluateAsync(ctx.Database, new
            {
                cursor = (RedisValue)cursor,
                pageSize = (RedisValue)optionsMonitor.CurrentValue.EnvironmentKeyUsagePageSize
            });

            if (evaluationResult.IsNull || evaluationResult.Resp2Type != ResultType.Array)
            {
                logger.LogError("Unexpected result from Lua script for key usage calculation. Expected an array, got {Type}.", evaluationResult.Resp2Type);
                break;
            }

            var result = (RedisResult[]?)evaluationResult;
            if (result is null || result.Length != 2)
            {
                logger.LogError("Unexpected result from Lua script for key usage calculation. Expected 2 elements, got {Length}.", result?.Length ?? 0);
                break;
            }

            cursor = (string)result[0]!;

            var flatResult = result[1];
            if (flatResult.IsNull || flatResult.Resp2Type != ResultType.Array)
            {
                logger.LogError("Unexpected result format from Lua script for key usage calculation. Expected an array, got {Type}.", flatResult.Resp2Type);
                break;
            }

            var flat = (RedisResult[]?)flatResult;
            if (flat is null)
            {
                logger.LogError("Unexpected null result from Lua script for key usage calculation.");
                break;
            }

            for (int i = 0; i < flat.Length - 1; i += 2)
            {
                var prefix = (string)flat[i]!;
                var count = long.Parse((string)flat[i + 1]!);
                totalCounts[prefix] = totalCounts.TryGetValue(prefix, out var n) ? n + count : count;
            }
        } while (cursor != "0");

        return totalCounts;
    }
    private async Task<Dictionary<string, int>> RunLuaAsync(RedisContext ctx)
    {
        string luaSource = @"
                             --Args: ARGV[1] = cursor, ARGV[2] = page size
                             -- Returns: { next_cursor, { segment_key, count, ...} }
                             local result = redis.call('SCAN', @cursor, 'MATCH', '*:big_segment_*:*', 'COUNT', @pageSize)
                             local newCursor = result[1]
                             local keys = result[2]
                             local counts = { }
                             
                             for _, key in ipairs(keys) do
                                         local env, kind = key:match('^([^:]+):([^:]+):')
                                 if env and kind then
                                     local members = redis.call('SMEMBERS', key)
                                     for _, member in ipairs(members) do
                                         local countKey = env..'|'..kind..'|'..member
                                         counts[countKey] = (counts[countKey] or 0) +1
                                     end
                                 end
                             end
                             
                             local flat = { }
                                     for k, v in pairs(counts) do
                                         table.insert(flat, k)
                                     
                                         table.insert(flat, tostring(v))
                             end
                             
                             return { newCursor, flat}
                            ";


        var script = LuaScript.Prepare(luaSource);
        var loaded = script.Load(ctx.Server);
        var cursor = "0";
        var totalCounts = new Dictionary<string, int>();

        do
        {
            var evaluationResult = await loaded.EvaluateAsync(ctx.Database, new
            {
                cursor = (RedisValue)cursor,
                pageSize = (RedisValue)optionsMonitor.CurrentValue.BigSegmentKeyUsagePageSize
            });

            if (evaluationResult.IsNull || evaluationResult.Resp2Type != ResultType.Array)
            {
                logger.LogError("Unexpected result from Lua script for big segment membership calculation. Expected an array, got {Type}.", evaluationResult.Resp2Type);
                break;
            }

            var result = (RedisResult[]?)evaluationResult;
            if (result is null || result.Length != 2)
            {
                logger.LogError("Unexpected result from Lua script for big segment membership calculation. Expected 2 elements, got {Length}.", result?.Length ?? 0);
                break;
            }

            cursor = (string)result[0]!;

            var flatResult = result[1];
            if (flatResult.IsNull || flatResult.Resp2Type != ResultType.Array)
            {
                logger.LogError("Unexpected result format from Lua script for big segment membership calculation. Expected an array, got {Type}.", flatResult.Resp2Type);
                break;
            }

            var flat = (RedisResult[]?)flatResult;
            if (flat is null)
            {
                logger.LogError("Unexpected null result from Lua script for big segment membership calculation.");
                break;
            }

            for (int i = 0; i < flat.Length - 1; i += 2)
            {
                var key = (string)flat[i]!;
                var count = (int)flat[i + 1];
                totalCounts[key] = totalCounts.TryGetValue(key, out var n) ? n + count : count;
            }
        } while (cursor != "0");

        return totalCounts;
    }
}

struct AuxillaryClientInfo
{
    public ClientInfo ClientInfo { get; set; }
    public string? User { get; set; }
    public long? TotalNetworkIn { get; set; }
    public long? TotalNetworkOut { get; set; }

    /// <summary>
    /// Parses the client info string to extract user, total network in, and total network out
    /// StackExchange.Redis.ClientInfo does not expose all these properties natively, so we have to parse the raw string ourselves.
    /// </summary>
    public static AuxillaryClientInfo FromClientInfo(ClientInfo clientInfo)
    {
        ReadOnlySpan<char> rawString = clientInfo.Raw;
        AuxillaryClientInfo aci = new() { ClientInfo = clientInfo };

        foreach (var range in rawString.Split(' '))
        {
            var pair = rawString[range];
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;

            var key = pair[..eq];
            var val = pair[(eq + 1)..].ToString();

            if (key.Equals("user", StringComparison.Ordinal)) aci.User = val;
            else if (key.Equals("tot-net-in", StringComparison.Ordinal)) aci.TotalNetworkIn = Convert.ToInt64(val);
            else if (key.Equals("tot-net-out", StringComparison.Ordinal)) aci.TotalNetworkOut = Convert.ToInt64(val);
        }

        return aci;
    }
}
