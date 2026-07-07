using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LDPersistentTelemetry.Models;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace LDPersistentTelemetry.Context;

public interface IRelayContextProvider
{
    Task<RelayProxyAutoConfigCache> GetRelayContextAsync(CancellationToken cancellationToken = default);
}

public class RedisAutoConfigOptions
{
    public static readonly string Section = "RelayProxyAutoConfigCache";

    public required string AutoConfigCacheKey { get; init; }
    public required string EncryptionKey { get; init; }
}


// TODO: Consider subscribing to changes to the auto-config cache value via Redis pub/sub so we can be
//       notified when the cache is updated and refresh our in-memory copy of the context.
//       // Potentially this method could return a cached copy, and only refresh when notified on the change?
public class RedisAutoConfigContextProvider(IConnectionMultiplexer redisConnection,
                                            IOptionsMonitor<RedisAutoConfigOptions> optionsMonitor,
                                            ILogger<RedisAutoConfigContextProvider> _logger) : IRelayContextProvider
{
    public async Task<RelayProxyAutoConfigCache> GetRelayContextAsync(CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Fetching relay context from Redis cache with key `{CacheKey}`, decrypting with key ending in `{DecryptionKey}`.",
                optionsMonitor.CurrentValue.AutoConfigCacheKey,
                optionsMonitor.CurrentValue.EncryptionKey[^4..]);
        }

        var db = redisConnection.GetDatabase();

        var result = await db.HashGetAllAsync(optionsMonitor.CurrentValue.AutoConfigCacheKey);
        var keyBytes = Encoding.UTF8.GetBytes(optionsMonitor.CurrentValue.EncryptionKey);
        var environments = new Dictionary<string, Models.Environment>();

        foreach (var entry in result)
        {
            var decoded = Decrypt(entry.Value!, keyBytes);

            var jsonString = Encoding.UTF8.GetString(decoded);
            var wrappedConfig = JsonSerializer.Deserialize<CachedItem>(jsonString);
            var environment = wrappedConfig?.Data.Deserialize<Models.Environment>();

            _logger.LogInformation("Loading context for proj:{ProjectKey}/env:{EnvironmentKey}",
                                   environment!.ProjectKey, environment.Key);

            environments.Add(environment.ID, environment);
        }

        return new RelayProxyAutoConfigCache
        {
            Environments = environments
        };
    }

    #region Decryption Logic - https://github.com/launchdarkly/ld-relay/blob/3b16c8e9248b3a6c7b24302f1c5904ab2bade1e5/internal/autoconfigcache/crypto.go
    private const int AesKeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] InfoContext = Encoding.UTF8.GetBytes("ld-relay autoconfig cache");

    /// <summary>
    /// Decrypts the ciphertext using the derived HKDF key.
    /// </summary>
    public static byte[] Decrypt(byte[] ciphertext, byte[] encryptionKey)
    {
        if (ciphertext == null || ciphertext.Length < NonceSize + TagSize)
        {
            throw new ArgumentException("Ciphertext too short.");
        }

        byte[] derivedKey = DeriveKey(encryptionKey);

        ReadOnlySpan<byte> nonce = ciphertext.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> encryptedData = ciphertext.AsSpan(NonceSize, ciphertext.Length - NonceSize - TagSize);
        ReadOnlySpan<byte> tag = ciphertext.AsSpan(ciphertext.Length - TagSize);

        byte[] plaintext = new byte[encryptedData.Length];
        using (AesGcm aesGcm = new(derivedKey, TagSize))
        {
            aesGcm.Decrypt(nonce, encryptedData, tag, plaintext);
        }

        return plaintext;
    }

    /// <summary>
    /// Derives the 32-byte AES key using HKDF-SHA256 matching the Go implementation.
    /// </summary>
    public static byte[] DeriveKey(byte[] inputKeyMaterial)
    {
        // Go: hkdf.New(sha256.New, ikm, nil, []byte("ld-relay autoconfig cache"))
        // Note: The 'salt' parameter is null (nil in Go)
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, AesKeySize, salt: null, info: InfoContext);
    }
    #endregion Decryption Logic
}
