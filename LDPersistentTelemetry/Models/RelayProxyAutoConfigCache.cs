using System.Text.Json;
using System.Text.Json.Serialization;

namespace LDPersistentTelemetry.Models;

public readonly record struct RelayProxyAutoConfigCache
{
    [JsonPropertyName("environments")]
    public required Dictionary<string, Environment> Environments { get; init; }

}

public record ExpiringKey
{
    [JsonPropertyName("value")]
    public required string Value { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; } = 0; // Unix milliseconds
}

public record SdkKey
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("expiring")]
    public required ExpiringKey Expiring { get; init; }
}

public record Environment
{
    [JsonPropertyName("envID")]
    public required string ID { get; init; }


    [JsonPropertyName("envKey")]
    public required string Key { get; init; }

    [JsonPropertyName("envName")]
    public required string Name { get; init; }

    [JsonPropertyName("mobKey")]
    public required string MobileKey { get; init; }

    [JsonPropertyName("projName")]
    public required string ProjectName { get; init; }

    [JsonPropertyName("projKey")]
    public required string ProjectKey { get; init; }

    [JsonPropertyName("sdkKey")]
    public required SdkKey SDKKey { get; init; }

    [JsonPropertyName("defaultTtl")]
    public required int DefaultTTL { get; init; }

    [JsonPropertyName("secureMode")]
    public required bool SecureMode { get; init; }

    [JsonPropertyName("version")]
    public required int Version { get; init; }
}
public enum ModelKind
{
    Environment,
    Filter
}

public class CachedItem
{
    // Constant matching the Go package version
    public const int CurrentModelVersion = 1;

    // JsonConverter forces the enum to serialize as string values ("environment", "filter")
    // JsonPropertyName handles the camelCase naming convention matching Go's tags
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumWithLowerCamelCaseConverter))]
    public ModelKind Kind { get; set; }

    [JsonPropertyName("modelVersion")]
    public int ModelVersion { get; set; }

    // JsonElement maps perfectly to json.RawMessage, preserving unparsed raw JSON data
    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }
}

/// <summary>
/// Custom converter ensuring the enum values serialize precisely to "environment" and "filter".
/// </summary>
public class JsonStringEnumWithLowerCamelCaseConverter : JsonConverter<ModelKind>
{
    public override ModelKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        return value switch
        {
            "environment" => ModelKind.Environment,
            "filter" => ModelKind.Filter,
            _ => throw new JsonException($"Unknown ModelKind: {value}")
        };
    }

    public override void Write(Utf8JsonWriter writer, ModelKind value, JsonSerializerOptions options)
    {
        string stringValue = value switch
        {
            ModelKind.Environment => "environment",
            ModelKind.Filter => "filter",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        writer.WriteStringValue(stringValue);
    }
}
